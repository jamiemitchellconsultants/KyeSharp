using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace KyeSharpWASM.Pages;

public partial class Kye : IDisposable
{
    // ─── Constants ────────────────────────────────────────────────────────────
    const int Cols = 20, Rows = 15;
    const string ProgressStorageKey = "kye.progress.v1";
    const string CustomPackStorageKey = "kye.custom-packs.v1";
    const string PreviewLevelStorageKey = "kye.preview-level.v1";
    const string PreviewAutoSelectKey = "kye.preview-level-select.v1";
    const int MaxImportedPackBytes = 512_000;
    const int MaxImportedProgressBytes = 256_000;
    const int TileWidth = 40;
    const int TileHeight = 20;
    const int TileHalfWidth = TileWidth / 2;
    const int TileHalfHeight = TileHeight / 2;
    const int TileRenderHeight = 112;
    const int BoardInsetX = 24;
    const int BoardInsetY = 24;

    // ─── Cell type ────────────────────────────────────────────────────────────
    enum Cell { Empty, Wall, Soft, Diamond, Turner, AntiTurner, WormHole, ExitClosed, ExitOpen }
    enum ScreenDirection { NorthWest, NorthEast, SouthWest, SouthEast }

    sealed class LevelManifestDto
    {
        public List<LevelIndexEntryDto> Levels { get; set; } = [];
    }

    sealed class LevelIndexEntryDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? File { get; set; }
        public string? TierId { get; set; }
        public string? TierName { get; set; }
        public int TierOrder { get; set; }
        public int Order { get; set; }
        public string? PackId { get; set; }
        public string? PackName { get; set; }
        public int PackOrder { get; set; }
    }

    sealed class LevelFileDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Hint { get; set; }
        public string[]? Map { get; set; }
    }

    sealed class LevelDefinition
    {
        public required string Key { get; init; }
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string File { get; init; }
        public required string Hint { get; init; }
        public required string[] Map { get; init; }
        public required string TierId { get; init; }
        public required string TierName { get; init; }
        public required int TierOrder { get; init; }
        public required int Order { get; init; }
        public required string PackId { get; init; }
        public required string PackName { get; init; }
        public required int PackOrder { get; init; }
        public required bool IsCustom { get; init; }
        public string? PackAuthor { get; init; }
        public string? PackDescription { get; init; }
    }

    sealed class ProgressStateDto
    {
        public int Version { get; set; } = 1;
        public string? SelectedLevelKey { get; set; }
        public Dictionary<string, LevelProgressDto> Completed { get; set; } = [];
    }

    sealed class LevelProgressDto
    {
        public int BestScore { get; set; }
        public DateTime CompletedAtUtc { get; set; }
    }

    sealed class StoredCustomPacksDto
    {
        public int Version { get; set; } = 1;
        public List<CustomPackDto> Packs { get; set; } = [];
    }

    sealed class CustomPackDto
    {
        public int Version { get; set; } = 1;
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public List<CustomPackTierDto>? Tiers { get; set; }
    }

    sealed class CustomPackTierDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int Order { get; set; }
        public List<CustomPackLevelDto>? Levels { get; set; }
    }

    sealed class CustomPackLevelDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Hint { get; set; }
        public int Order { get; set; }
        public string[]? Map { get; set; }
    }

    // ─── Enemy structs ────────────────────────────────────────────────────────
    record struct Roundel(int X, int Y, int Dx, int Dy);
    record struct Rocky(int X, int Y, int Dx, int Dy);
    record struct Pusher(int X, int Y, int Dx, int Dy);

    // ─── Game state ───────────────────────────────────────────────────────────
    Cell[,] _grid = new Cell[Rows, Cols];
    int _px, _py;                          // player position (col, row)
    List<Roundel> _roundels = new();
    List<Rocky>   _rockies  = new();
    List<Pusher>  _pushers  = new();
    readonly List<(int X, int Y)> _wormHoles = [];
    int  _diamondsLeft;
    int  _score;
    int  _level = 1;
    bool _gameOver;
    bool _levelComplete;
    int  _tick;
    int  _playerHopStamp;
    string? _levelName;
    string? _levelHint;
    string? _levelLoadError;
    string? _importStatusMessage;
    bool _importStatusIsError;
    string? _progressStatusMessage;
    bool _progressStatusIsError;
    bool _isLoadingLevels = true;
    bool _shouldFocusBoard;
    readonly List<LevelDefinition> _levels = [];
    readonly List<CustomPackDto> _customPacks = [];
    ProgressStateDto _progress = new();
    List<int> _roundelHopStamps = new();
    List<int> _rockyHopStamps = new();
    List<int> _pusherHopStamps = new();

    Timer?           _timer;
    ElementReference _boardRef;

    [Inject]
    HttpClient Http { get; set; } = default!;

    [Inject]
    IJSRuntime JS { get; set; } = default!;

    // ─── Lifecycle ────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        await LoadStoredProgressAsync();
        await LoadStoredCustomPacksAsync();
        await ReloadLevelsAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_shouldFocusBoard && CanPlay)
        {
            _shouldFocusBoard = false;
            await _boardRef.FocusAsync();
        }
    }

    public void Dispose() => _timer?.Dispose();

    // ─── Init / Level loading ─────────────────────────────────────────────────
    int TotalLevels => _levels.Count;
    bool CanPlay => !_isLoadingLevels && _levelLoadError is null && _levels.Count > 0;
    string CurrentLevelKey => _level > 0 && _level <= _levels.Count ? _levels[_level - 1].Key : string.Empty;
    LevelDefinition? CurrentLevel => _level > 0 && _level <= _levels.Count ? _levels[_level - 1] : null;
    int CompletedLevels => _levels.Count(level => _progress.Completed.ContainsKey(level.Key));
    bool HasImportedPacks => _customPacks.Count > 0;
    IEnumerable<LevelDefinition> OrderedLevels => _levels
        .OrderBy(level => level.PackOrder)
        .ThenBy(level => level.PackName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(level => level.TierOrder)
        .ThenBy(level => level.TierName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(level => level.Order)
        .ThenBy(level => level.Name, StringComparer.OrdinalIgnoreCase);

    void InitGame()
    {
        if (_levels.Count == 0)
            return;

        _score = 0;
        _level = ResolveInitialLevelIndex();
        LoadCurrentLevel();
        StartTimer();
        _shouldFocusBoard = true;
        RememberSelectedLevel();
        QueueSaveProgress();
    }

    async Task ReloadLevelsAsync()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _isLoadingLevels = true;
        _levelLoadError = null;

        try
        {
            var manifest = await Http.GetFromJsonAsync<LevelManifestDto>("levels/index.json");
            if (manifest?.Levels is null || manifest.Levels.Count == 0)
                throw new InvalidOperationException("No levels were found in levels/index.json.");

            var loaded = new List<LevelDefinition>(manifest.Levels.Count);
            foreach (var entry in manifest.Levels)
            {
                loaded.Add(await LoadLevelDefinitionAsync(entry));
            }

            loaded.AddRange(BuildCustomLevels(_customPacks));

            var previewLevel = await LoadEditorPreviewLevelAsync();
            if (previewLevel is not null)
                loaded.Insert(0, previewLevel);

            EnsureUniqueLevelKeys(loaded);

            _levels.Clear();
            _levels.AddRange(loaded.OrderBy(level => level.PackOrder)
                .ThenBy(level => level.TierOrder)
                .ThenBy(level => level.Order)
                .ThenBy(level => level.Name, StringComparer.OrdinalIgnoreCase));

            var forcedPreviewKey = await JS.InvokeAsync<string?>("localStorage.getItem", PreviewAutoSelectKey);
            if (!string.IsNullOrWhiteSpace(forcedPreviewKey) && _levels.Any(level => level.Key == forcedPreviewKey))
            {
                _progress.SelectedLevelKey = forcedPreviewKey;
                await JS.InvokeVoidAsync("localStorage.removeItem", PreviewAutoSelectKey);
            }

            InitGame();
        }
        catch (Exception ex)
        {
            _levels.Clear();
            _level = 0;
            _score = 0;
            _levelName = null;
            _levelHint = null;
            _levelLoadError = ex.Message;
        }
        finally
        {
            _isLoadingLevels = false;
        }
    }

    async Task LoadStoredProgressAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", ProgressStorageKey);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var loaded = JsonSerializer.Deserialize<ProgressStateDto>(json);
            if (loaded?.Version == 1)
                _progress = NormalizeProgress(loaded);
        }
        catch
        {
            _progress = new ProgressStateDto();
        }
    }

    async Task LoadStoredCustomPacksAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", CustomPackStorageKey);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var loaded = JsonSerializer.Deserialize<StoredCustomPacksDto>(json);
            if (loaded?.Version != 1 || loaded.Packs is null)
                return;

            _customPacks.Clear();
            foreach (var pack in loaded.Packs)
            {
                ValidateCustomPack(pack, allowBuiltinPackId: false);
                _customPacks.Add(pack);
            }
        }
        catch (Exception ex)
        {
            _customPacks.Clear();
            _importStatusIsError = true;
            _importStatusMessage = $"Stored custom packs were ignored because they are invalid: {ex.Message}";
            await JS.InvokeVoidAsync("localStorage.removeItem", CustomPackStorageKey);
        }
    }

    async Task SaveProgressAsync()
    {
        var json = JsonSerializer.Serialize(_progress);
        await JS.InvokeVoidAsync("localStorage.setItem", ProgressStorageKey, json);
    }

    static ProgressStateDto NormalizeProgress(ProgressStateDto? input)
    {
        var normalized = new ProgressStateDto
        {
            Version = 1,
            SelectedLevelKey = string.IsNullOrWhiteSpace(input?.SelectedLevelKey) ? null : input.SelectedLevelKey.Trim(),
            Completed = [],
        };

        if (input?.Completed is null)
            return normalized;

        foreach (var (key, value) in input.Completed)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;

            normalized.Completed[key.Trim()] = new LevelProgressDto
            {
                BestScore = Math.Max(0, value.BestScore),
                CompletedAtUtc = value.CompletedAtUtc == default ? DateTime.UtcNow : value.CompletedAtUtc,
            };
        }

        return normalized;
    }

    async Task SaveCustomPacksAsync()
    {
        var json = JsonSerializer.Serialize(new StoredCustomPacksDto { Packs = _customPacks.ToList() });
        await JS.InvokeVoidAsync("localStorage.setItem", CustomPackStorageKey, json);
    }

    void QueueSaveProgress() => _ = InvokeAsync(SaveProgressAsync);

    int ResolveInitialLevelIndex()
    {
        if (!string.IsNullOrWhiteSpace(_progress.SelectedLevelKey))
        {
            var selectedIndex = _levels.FindIndex(level => level.Key == _progress.SelectedLevelKey);
            if (selectedIndex >= 0)
                return selectedIndex + 1;
        }

        var nextIncomplete = OrderedLevels.FirstOrDefault(level => !_progress.Completed.ContainsKey(level.Key));
        if (nextIncomplete is not null)
            return _levels.FindIndex(level => level.Key == nextIncomplete.Key) + 1;

        return 1;
    }

    void RememberSelectedLevel()
    {
        if (CurrentLevel is null)
            return;

        _progress.SelectedLevelKey = CurrentLevel.Key;
    }

    async Task<LevelDefinition> LoadLevelDefinitionAsync(LevelIndexEntryDto entry)
    {
        var file = entry.File?.Trim();
        if (string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException("Every level entry in levels/index.json must include a file name.");

        var levelFile = await Http.GetFromJsonAsync<LevelFileDto>($"levels/{file}");
        if (levelFile is null)
            throw new InvalidOperationException($"Failed to load level file 'levels/{file}'.");

        var id = string.IsNullOrWhiteSpace(entry.Id) ? levelFile.Id?.Trim() : entry.Id.Trim();
        var name = string.IsNullOrWhiteSpace(levelFile.Name) ? entry.Name?.Trim() : levelFile.Name.Trim();

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Level file '{file}' must define a non-empty id.");

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"Level file '{file}' must define a non-empty name.");

        if (levelFile.Map is null)
            throw new InvalidOperationException($"Level file '{file}' is missing its map array.");

        ValidateLevelMap(levelFile.Map, file);

        var tierId = entry.TierId?.Trim();
        var tierName = entry.TierName?.Trim();
        if (string.IsNullOrWhiteSpace(tierId) || string.IsNullOrWhiteSpace(tierName))
            throw new InvalidOperationException($"Manifest entry for '{file}' must define tierId and tierName.");

        var packId = string.IsNullOrWhiteSpace(entry.PackId) ? "builtin" : entry.PackId.Trim();
        var packName = string.IsNullOrWhiteSpace(entry.PackName) ? "Core Pack" : entry.PackName.Trim();

        return new LevelDefinition
        {
            Key = ComposeLevelKey(packId, id),
            Id = id,
            Name = name,
            File = file,
            Hint = levelFile.Hint?.Trim() ?? string.Empty,
            Map = levelFile.Map,
            TierId = tierId,
            TierName = tierName,
            TierOrder = entry.TierOrder,
            Order = entry.Order,
            PackId = packId,
            PackName = packName,
            PackOrder = entry.PackOrder,
            IsCustom = false,
        };
    }

    static string ComposeLevelKey(string packId, string levelId) => $"{packId}:{levelId}";

    async Task<LevelDefinition?> LoadEditorPreviewLevelAsync()
    {
        var json = await JS.InvokeAsync<string?>("localStorage.getItem", PreviewLevelStorageKey);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var level = JsonSerializer.Deserialize<LevelFileDto>(json);
            if (level?.Map is null)
                return null;

            ValidateLevelMap(level.Map, "editor preview");

            var id = string.IsNullOrWhiteSpace(level.Id) ? "editor-level" : level.Id.Trim();
            var name = string.IsNullOrWhiteSpace(level.Name) ? "Editor Preview" : level.Name.Trim();

            return new LevelDefinition
            {
                Key = ComposeLevelKey("editor-preview", id),
                Id = id,
                Name = name,
                File = "editor-preview",
                Hint = level.Hint?.Trim() ?? "Loaded from the WYSIWYG editor.",
                Map = level.Map,
                TierId = "preview",
                TierName = "Preview",
                TierOrder = -1,
                Order = 0,
                PackId = "editor-preview",
                PackName = "Editor Preview",
                PackOrder = -1,
                IsCustom = true,
                PackAuthor = "Local editor",
            };
        }
        catch
        {
            return null;
        }
    }

    static void EnsureUniqueLevelKeys(IEnumerable<LevelDefinition> levels)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in levels)
        {
            if (!seen.Add(level.Key))
                throw new InvalidOperationException($"Duplicate level key '{level.Key}' was found while building the catalog.");
        }
    }

    List<LevelDefinition> BuildCustomLevels(IEnumerable<CustomPackDto> packs)
    {
        var built = new List<LevelDefinition>();
        var packIndex = 0;

        foreach (var pack in packs)
        {
            ValidateCustomPack(pack, allowBuiltinPackId: false);

            var packId = pack.Id!.Trim();
            var packName = pack.Name!.Trim();
            var packAuthor = string.IsNullOrWhiteSpace(pack.Author) ? null : pack.Author.Trim();
            var packDescription = string.IsNullOrWhiteSpace(pack.Description) ? null : pack.Description.Trim();

            foreach (var tier in pack.Tiers!.OrderBy(tier => tier.Order).ThenBy(tier => tier.Name, StringComparer.OrdinalIgnoreCase))
            {
                var tierId = tier.Id!.Trim();
                var tierName = tier.Name!.Trim();

                foreach (var level in tier.Levels!.OrderBy(level => level.Order).ThenBy(level => level.Name, StringComparer.OrdinalIgnoreCase))
                {
                    ValidateLevelMap(level.Map!, $"custom pack '{packId}' level '{level.Id}'");

                    built.Add(new LevelDefinition
                    {
                        Key = ComposeLevelKey(packId, level.Id!.Trim()),
                        Id = level.Id.Trim(),
                        Name = level.Name!.Trim(),
                        File = $"import:{packId}/{tierId}/{level.Id!.Trim()}",
                        Hint = level.Hint?.Trim() ?? string.Empty,
                        Map = level.Map!,
                        TierId = tierId,
                        TierName = tierName,
                        TierOrder = tier.Order,
                        Order = level.Order,
                        PackId = packId,
                        PackName = packName,
                        PackOrder = 100 + packIndex,
                        IsCustom = true,
                        PackAuthor = packAuthor,
                        PackDescription = packDescription,
                    });
                }
            }

            packIndex++;
        }

        return built;
    }

    static void ValidateCustomPack(CustomPackDto pack, bool allowBuiltinPackId)
    {
        if (pack.Version != 1)
            throw new InvalidOperationException("Custom pack version must be 1.");

        if (string.IsNullOrWhiteSpace(pack.Id))
            throw new InvalidOperationException("Custom pack id is required.");

        if (!allowBuiltinPackId && string.Equals(pack.Id.Trim(), "builtin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Custom pack id 'builtin' is reserved.");

        if (string.IsNullOrWhiteSpace(pack.Name))
            throw new InvalidOperationException("Custom pack name is required.");

        if (pack.Tiers is null || pack.Tiers.Count == 0)
            throw new InvalidOperationException($"Custom pack '{pack.Id}' must contain at least one tier.");

        var tierIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var levelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tier in pack.Tiers)
        {
            if (string.IsNullOrWhiteSpace(tier.Id) || string.IsNullOrWhiteSpace(tier.Name))
                throw new InvalidOperationException($"Every tier in custom pack '{pack.Id}' must define id and name.");

            if (!tierIds.Add(tier.Id.Trim()))
                throw new InvalidOperationException($"Custom pack '{pack.Id}' contains duplicate tier id '{tier.Id}'.");

            if (tier.Levels is null || tier.Levels.Count == 0)
                throw new InvalidOperationException($"Tier '{tier.Id}' in custom pack '{pack.Id}' must contain at least one level.");

            foreach (var level in tier.Levels)
            {
                if (string.IsNullOrWhiteSpace(level.Id) || string.IsNullOrWhiteSpace(level.Name))
                    throw new InvalidOperationException($"Every level in custom pack '{pack.Id}' must define id and name.");

                if (level.Map is null)
                    throw new InvalidOperationException($"Level '{level.Id}' in custom pack '{pack.Id}' must contain a map array.");

                if (!levelKeys.Add(ComposeLevelKey(pack.Id.Trim(), level.Id.Trim())))
                    throw new InvalidOperationException($"Custom pack '{pack.Id}' contains duplicate level id '{level.Id}'.");

                ValidateLevelMap(level.Map, $"custom pack '{pack.Id}' level '{level.Id}'");
            }
        }
    }

    static void ValidateLevelMap(string[] map, string file)
    {
        if (map.Length != Rows)
            throw new InvalidOperationException($"Level file '{file}' must contain exactly {Rows} map rows.");

        var playerCount = 0;
        var exitCount = 0;
        var wormHoleCount = 0;

        foreach (var (line, row) in map.Select((line, row) => (line, row)))
        {
            if (line.Length != Cols)
                throw new InvalidOperationException($"Level file '{file}' row {row + 1} must be exactly {Cols} characters wide.");

            foreach (var ch in line)
            {
                switch (ch)
                {
                    case '#':
                    case '.':
                    case '*':
                    case 'E':
                    case 'P':
                    case 'r':
                    case 'b':
                    case 'v':
                    case 's':
                    case 't':
                    case 'a':
                    case 'w':
                    case 'L':
                    case 'R':
                    case 'U':
                    case 'D':
                        break;
                    default:
                        throw new InvalidOperationException($"Level file '{file}' contains unsupported tile '{ch}'.");
                }

                if (ch == 'P') playerCount++;
                if (ch == 'E') exitCount++;
                if (ch == 'w') wormHoleCount++;
            }
        }

        if (playerCount != 1)
            throw new InvalidOperationException($"Level file '{file}' must contain exactly one player start 'P'.");

        if (exitCount != 1)
            throw new InvalidOperationException($"Level file '{file}' must contain exactly one exit 'E'.");

        if (wormHoleCount is not 0 and not 2)
            throw new InvalidOperationException($"Level file '{file}' must contain either 0 or 2 worm holes 'w'.");
    }

    void LoadCurrentLevel()
    {
        _roundels.Clear();
        _rockies.Clear();
        _pushers.Clear();
        _wormHoles.Clear();
        _roundelHopStamps.Clear();
        _rockyHopStamps.Clear();
        _pusherHopStamps.Clear();
        _diamondsLeft = 0;
        _gameOver = false;
        _levelComplete = false;
        _tick = 0;
        _playerHopStamp = 0;

        var level = _levels[_level - 1];
        _levelName = level.Name;
        _levelHint = level.Hint;
        RememberSelectedLevel();
        var map = level.Map;

        for (int row = 0; row < Rows; row++)
        {
            string line = map[row];
            for (int col = 0; col < Cols; col++)
            {
                char ch = line[col];
                switch (ch)
                {
                    case '#': _grid[row, col] = Cell.Wall;    break;
                    case 's': _grid[row, col] = Cell.Soft;    break;
                    case '*': _grid[row, col] = Cell.Diamond; _diamondsLeft++; break;
                    case 't': _grid[row, col] = Cell.Turner;      break;
                    case 'a': _grid[row, col] = Cell.AntiTurner;  break;
                    case 'w': _grid[row, col] = Cell.WormHole; _wormHoles.Add((col, row)); break;
                    case 'E': _grid[row, col] = Cell.ExitClosed; break;
                    case 'P': _grid[row, col] = Cell.Empty; _px = col; _py = row; break;
                    case 'r': _grid[row, col] = Cell.Empty; _roundels.Add(new(col, row, 0, 0)); _roundelHopStamps.Add(0); break;
                    case 'b': _grid[row, col] = Cell.Empty; _rockies.Add(new(col, row, 1, 0)); _rockyHopStamps.Add(0); break;
                    case 'v': _grid[row, col] = Cell.Empty; _rockies.Add(new(col, row, 0, 1)); _rockyHopStamps.Add(0); break;
                    case 'L': _grid[row, col] = Cell.Empty; _pushers.Add(new(col, row, -1, 0)); _pusherHopStamps.Add(0); break;
                    case 'R': _grid[row, col] = Cell.Empty; _pushers.Add(new(col, row, 1, 0)); _pusherHopStamps.Add(0); break;
                    case 'U': _grid[row, col] = Cell.Empty; _pushers.Add(new(col, row, 0, -1)); _pusherHopStamps.Add(0); break;
                    case 'D': _grid[row, col] = Cell.Empty; _pushers.Add(new(col, row, 0, 1)); _pusherHopStamps.Add(0); break;
                    default:  _grid[row, col] = Cell.Empty; break;
                }
            }
        }

        // Open exit immediately if there are no diamonds (shouldn't happen but safe)
        if (_diamondsLeft == 0) OpenExit();

        QueueSaveProgress();
    }

    void MarkCurrentLevelCompleted()
    {
        if (CurrentLevel is null)
            return;

        if (_progress.Completed.TryGetValue(CurrentLevel.Key, out var existing))
        {
            existing.BestScore = Math.Max(existing.BestScore, _score);
            existing.CompletedAtUtc = DateTime.UtcNow;
        }
        else
        {
            _progress.Completed[CurrentLevel.Key] = new LevelProgressDto
            {
                BestScore = _score,
                CompletedAtUtc = DateTime.UtcNow,
            };
        }

        QueueSaveProgress();
    }

    void StartTimer()
    {
        _timer?.Dispose();
        _timer = new Timer(_ =>
        {
            InvokeAsync(() =>
            {
                if (!_gameOver && !_levelComplete)
                {
                    Tick();
                    StateHasChanged();
                }
            });
        }, null, 600, 320);
    }

    // ─── Game tick (enemy movement) ───────────────────────────────────────────
    void Tick()
    {
        _tick++;
        MovePushers();
        MoveRockies();
        if (_tick % 2 == 0) MoveRoundels();
        CheckEnemyCollisions();
    }

    void MovePushers()
    {
        for (int i = 0; i < _pushers.Count; i++)
        {
            var p = _pushers[i];
            var dx = p.Dx;
            var dy = p.Dy;
            var nx = p.X + dx;
            var ny = p.Y + dy;

            if (nx < 0 || nx >= Cols || ny < 0 || ny >= Rows)
            {
                _pushers[i] = new(p.X, p.Y, -dx, -dy);
                continue;
            }

            if (IsNonPusherActorAt(nx, ny) || IsPusherAt(nx, ny, i))
                continue;

            var target = _grid[ny, nx];
            if (target == Cell.ExitClosed || target == Cell.ExitOpen)
            {
                _pushers[i] = new(p.X, p.Y, -dx, -dy);
                continue;
            }

            if (IsPushableByPusher(target) && !TryPushTile(nx, ny, dx, dy))
            {
                _pushers[i] = new(p.X, p.Y, -dx, -dy);
                continue;
            }

            var nextDx = dx;
            var nextDy = dy;
            var landed = _grid[ny, nx];
            if (landed == Cell.Turner)
                (nextDx, nextDy) = RotateClockwise(dx, dy);
            else if (landed == Cell.AntiTurner)
                (nextDx, nextDy) = RotateAntiClockwise(dx, dy);

            _pushers[i] = new(nx, ny, nextDx, nextDy);
            _pusherHopStamps[i]++;
        }
    }

    bool TryPushTile(int tileX, int tileY, int dx, int dy)
    {
        var movedTile = _grid[tileY, tileX];
        if (movedTile == Cell.Wall && IsBoardEdge(tileX, tileY))
            return false;

        var destX = tileX + dx;
        var destY = tileY + dy;

        if (destX < 0 || destX >= Cols || destY < 0 || destY >= Rows)
            return false;

        if (_grid[destY, destX] != Cell.Empty)
            return false;

        if (IsAnyActorAt(destX, destY) || (_px == destX && _py == destY))
            return false;

        _grid[tileY, tileX] = Cell.Empty;
        _grid[destY, destX] = movedTile;

        if (movedTile == Cell.WormHole)
            RefreshWormHoles();

        return true;
    }

    static bool IsPushableByPusher(Cell cell)
        => cell is Cell.Wall or Cell.Soft or Cell.Diamond or Cell.Turner or Cell.AntiTurner or Cell.WormHole;

    static bool IsBoardEdge(int col, int row)
        => row == 0 || row == Rows - 1 || col == 0 || col == Cols - 1;

    bool IsAnyActorAt(int col, int row)
        => _roundels.Any(r => r.X == col && r.Y == row)
           || _rockies.Any(r => r.X == col && r.Y == row)
           || _pushers.Any(r => r.X == col && r.Y == row);

    bool IsNonPusherActorAt(int col, int row)
        => _roundels.Any(r => r.X == col && r.Y == row)
           || _rockies.Any(r => r.X == col && r.Y == row);

    bool IsPusherAt(int col, int row, int exceptIndex)
    {
        for (int i = 0; i < _pushers.Count; i++)
        {
            if (i == exceptIndex)
                continue;
            if (_pushers[i].X == col && _pushers[i].Y == row)
                return true;
        }

        return false;
    }

    void RefreshWormHoles()
    {
        _wormHoles.Clear();
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Cols; col++)
                if (_grid[row, col] == Cell.WormHole)
                    _wormHoles.Add((col, row));
    }

    void MoveRockies()
    {
        for (int i = 0; i < _rockies.Count; i++)
        {
            var rk = _rockies[i];
            int nx = rk.X + rk.Dx, ny = rk.Y + rk.Dy;
            int dx = rk.Dx, dy = rk.Dy;

            if (!EnemyCanEnter(ny, nx))
            {
                // Reverse and try
                dx = -rk.Dx; dy = -rk.Dy;
                nx = rk.X + dx; ny = rk.Y + dy;
                if (!EnemyCanEnter(ny, nx)) { nx = rk.X; ny = rk.Y; dx = rk.Dx; dy = rk.Dy; }
            }

            if (nx != rk.X || ny != rk.Y)
            {
                if (_grid[ny, nx] == Cell.Turner)
                    (dx, dy) = RotateClockwise(dx, dy);
                else if (_grid[ny, nx] == Cell.AntiTurner)
                    (dx, dy) = RotateAntiClockwise(dx, dy);

                _rockyHopStamps[i]++;
            }

            _rockies[i] = new(nx, ny, dx, dy);
        }
    }

    void MoveRoundels()
    {
        for (int i = 0; i < _roundels.Count; i++)
        {
            var rl = _roundels[i];
            int adx = Math.Abs(_px - rl.X), ady = Math.Abs(_py - rl.Y);
            int sx = Math.Sign(_px - rl.X), sy = Math.Sign(_py - rl.Y);

            // Priority: move along larger axis first, then smaller, then reverses
            int[] dxs, dys;
            if (adx >= ady)
            { dxs = new[] { sx, 0, -sx, 0 }; dys = new[] { 0, sy, 0, -sy }; }
            else
            { dxs = new[] { 0, sx, 0, -sx }; dys = new[] { sy, 0, -sy, 0 }; }

            var candidates = new List<(int Dx, int Dy)>(5);
            if (rl.Dx != 0 || rl.Dy != 0)
                candidates.Add((rl.Dx, rl.Dy));

            for (int t = 0; t < 4; t++)
            {
                if (dxs[t] == 0 && dys[t] == 0) continue;
                var candidate = (dxs[t], dys[t]);
                if (!candidates.Contains(candidate))
                    candidates.Add(candidate);
            }

            for (int t = 0; t < candidates.Count; t++)
            {
                var (stepDx, stepDy) = candidates[t];
                int nx = rl.X + stepDx, ny = rl.Y + stepDy;
                if (EnemyCanEnterForRoundel(ny, nx, i))
                {
                    if (nx != rl.X || ny != rl.Y)
                        _roundelHopStamps[i]++;

                    var nextDx = stepDx;
                    var nextDy = stepDy;
                    if (_grid[ny, nx] == Cell.Turner)
                        (nextDx, nextDy) = RotateClockwise(stepDx, stepDy);
                    else if (_grid[ny, nx] == Cell.AntiTurner)
                        (nextDx, nextDy) = RotateAntiClockwise(stepDx, stepDy);

                    _roundels[i] = new(nx, ny, nextDx, nextDy);
                    break;
                }
            }
        }
    }

    static (int Dx, int Dy) RotateClockwise(int dx, int dy) => (-dy, dx);

    static (int Dx, int Dy) RotateAntiClockwise(int dx, int dy) => (dy, -dx);

    void CheckEnemyCollisions()
    {
        if (_roundels.Any(r => r.X == _px && r.Y == _py) ||
            _rockies.Any(r => r.X == _px && r.Y == _py) ||
            _pushers.Any(r => r.X == _px && r.Y == _py))
            Die();
    }

    // ─── Player movement ──────────────────────────────────────────────────────
    void TryMove(int dx, int dy)
    {
        if (!CanPlay || _gameOver || _levelComplete) return;

        int nx = _px + dx, ny = _py + dy;
        if (nx < 0 || nx >= Cols || ny < 0 || ny >= Rows) return;

        var target = _grid[ny, nx];

        if (target == Cell.Wall) return;

        if (target == Cell.Soft)
        {
            // Try to push the soft wall one more step
            int px2 = nx + dx, py2 = ny + dy;
            if (px2 < 0 || px2 >= Cols || py2 < 0 || py2 >= Rows) return;
            if (_grid[py2, px2] != Cell.Empty) return;
            if (_roundels.Any(r => r.X == px2 && r.Y == py2)) return;
            if (_rockies.Any(r => r.X == px2 && r.Y == py2)) return;
            if (_pushers.Any(r => r.X == px2 && r.Y == py2)) return;
            _grid[py2, px2] = Cell.Soft;
            _grid[ny, nx] = Cell.Empty;
        }

        if (_grid[ny, nx] == Cell.ExitClosed) return;

        if (_grid[ny, nx] == Cell.ExitOpen)
        {
            _px = nx; _py = ny;
            _playerHopStamp++;
            _levelComplete = true;
            MarkCurrentLevelCompleted();
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var fx = nx;
        var fy = ny;

        if (_grid[ny, nx] == Cell.WormHole)
        {
            var destination = GetPairedWormHole(nx, ny);
            if (destination is not null)
            {
                fx = destination.Value.X;
                fy = destination.Value.Y;
            }
        }

        // Collide with enemy after movement/teleport resolution
        if (_roundels.Any(r => r.X == fx && r.Y == fy) ||
            _rockies.Any(r => r.X == fx && r.Y == fy) ||
            _pushers.Any(r => r.X == fx && r.Y == fy))
        { Die(); return; }

        // Collect diamond
        if (_grid[fy, fx] == Cell.Diamond)
        {
            _grid[fy, fx] = Cell.Empty;
            _diamondsLeft--;
            _score += 10;
            if (_diamondsLeft == 0) OpenExit();
        }

        _px = fx; _py = fy;
        _playerHopStamp++;
        StateHasChanged();
    }

    (int X, int Y)? GetPairedWormHole(int fromX, int fromY)
    {
        if (_wormHoles.Count != 2)
            return null;

        if (_wormHoles[0].X == fromX && _wormHoles[0].Y == fromY)
            return _wormHoles[1];

        if (_wormHoles[1].X == fromX && _wormHoles[1].Y == fromY)
            return _wormHoles[0];

        return null;
    }

    void Die()
    {
        _gameOver = true;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    void OpenExit()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (_grid[r, c] == Cell.ExitClosed)
                    _grid[r, c] = Cell.ExitOpen;
    }

    // ─── Passability ──────────────────────────────────────────────────────────
    bool EnemyCanEnter(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return false;
        var c = _grid[row, col];
        if (c == Cell.Wall || c == Cell.Soft || c == Cell.ExitClosed || c == Cell.ExitOpen) return false;
        if (_rockies.Any(r => r.X == col && r.Y == row)) return false;
        if (_roundels.Any(r => r.X == col && r.Y == row)) return false;
        if (_pushers.Any(r => r.X == col && r.Y == row)) return false;
        return true;
    }

    bool EnemyCanEnterForRoundel(int row, int col, int selfIdx)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return false;
        var c = _grid[row, col];
        if (c == Cell.Wall || c == Cell.Soft || c == Cell.ExitClosed || c == Cell.ExitOpen) return false;
        if (_rockies.Any(r => r.X == col && r.Y == row)) return false;
        if (_pushers.Any(r => r.X == col && r.Y == row)) return false;
        for (int i = 0; i < _roundels.Count; i++)
            if (i != selfIdx && _roundels[i].X == col && _roundels[i].Y == row) return false;
        return true;
    }

    // ─── Input ────────────────────────────────────────────────────────────────
    void TryMoveScreen(ScreenDirection direction)
    {
        switch (direction)
        {
            case ScreenDirection.NorthWest:
                TryMove(-1, 0);
                break;
            case ScreenDirection.NorthEast:
                TryMove(0, -1);
                break;
            case ScreenDirection.SouthWest:
                TryMove(0, 1);
                break;
            case ScreenDirection.SouthEast:
                TryMove(1, 0);
                break;
        }
    }

    void HandleKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowLeft":
            case "a":
            case "A":
            case "q":
            case "Q":
                TryMoveScreen(ScreenDirection.NorthWest);
                break;

            case "ArrowUp":
            case "w":
            case "W":
            case "e":
            case "E":
                TryMoveScreen(ScreenDirection.NorthEast);
                break;

            case "ArrowDown":
            case "s":
            case "S":
            case "z":
            case "Z":
                TryMoveScreen(ScreenDirection.SouthWest);
                break;

            case "ArrowRight":
            case "d":
            case "D":
            case "c":
            case "C":
                TryMoveScreen(ScreenDirection.SouthEast);
                break;
        }
    }

    void MoveNorthWest() => TryMoveScreen(ScreenDirection.NorthWest);
    void MoveNorthEast() => TryMoveScreen(ScreenDirection.NorthEast);
    void MoveSouthWest() => TryMoveScreen(ScreenDirection.SouthWest);
    void MoveSouthEast() => TryMoveScreen(ScreenDirection.SouthEast);

    // ─── Navigation actions ───────────────────────────────────────────────────
    async Task NextLevel()
    {
        if (!CanPlay || _level >= TotalLevels)
            return;

        _level++;
        LoadCurrentLevel();
        StartTimer();
        _shouldFocusBoard = true;
        RememberSelectedLevel();
        await SaveProgressAsync();
        await InvokeAsync(StateHasChanged);
    }

    async Task RestartLevel()
    {
        if (!CanPlay)
            return;

        LoadCurrentLevel();
        StartTimer();
        _shouldFocusBoard = true;
        RememberSelectedLevel();
        await SaveProgressAsync();
        await InvokeAsync(StateHasChanged);
    }

    async Task RestartGame()
    {
        if (!CanPlay)
            return;

        _score = 0;
        _level = 1;
        LoadCurrentLevel();
        StartTimer();
        _shouldFocusBoard = true;
        RememberSelectedLevel();
        await SaveProgressAsync();
        await InvokeAsync(StateHasChanged);
    }

    async Task HandleLevelSelectedAsync(ChangeEventArgs e)
    {
        if (e.Value is not string selectedKey || string.IsNullOrWhiteSpace(selectedKey))
            return;

        var levelIndex = _levels.FindIndex(level => level.Key == selectedKey);
        if (levelIndex < 0)
            return;

        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _score = 0;
        _level = levelIndex + 1;
        LoadCurrentLevel();
        StartTimer();
        _shouldFocusBoard = true;
        RememberSelectedLevel();
        await SaveProgressAsync();
        await InvokeAsync(StateHasChanged);
    }

    async Task ResumeNextIncompleteAsync()
    {
        var nextIncomplete = OrderedLevels.FirstOrDefault(level => !_progress.Completed.ContainsKey(level.Key));
        if (nextIncomplete is null)
            return;

        var levelIndex = _levels.FindIndex(level => level.Key == nextIncomplete.Key);
        if (levelIndex < 0)
            return;

        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _score = 0;
        _level = levelIndex + 1;
        LoadCurrentLevel();
        StartTimer();
        _shouldFocusBoard = true;
        RememberSelectedLevel();
        await SaveProgressAsync();
        await InvokeAsync(StateHasChanged);
    }

    async Task ImportCustomPackAsync(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file is null)
            return;

        try
        {
            await using var stream = file.OpenReadStream(MaxImportedPackBytes);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var pack = JsonSerializer.Deserialize<CustomPackDto>(json)
                       ?? throw new InvalidOperationException("The selected file did not contain a valid custom pack JSON document.");

            ValidateCustomPack(pack, allowBuiltinPackId: false);

            var existingIndex = _customPacks.FindIndex(existing => string.Equals(existing.Id, pack.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                _customPacks[existingIndex] = pack;
            else
                _customPacks.Add(pack);

            await SaveCustomPacksAsync();
            _importStatusIsError = false;
            _importStatusMessage = $"Imported pack '{pack.Name}' ({pack.Tiers!.Sum(tier => tier.Levels?.Count ?? 0)} levels).";
            await ReloadLevelsAsync();
        }
        catch (Exception ex)
        {
            _importStatusIsError = true;
            _importStatusMessage = ex.Message;
        }
    }

    async Task ExportProgressAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_progress, new JsonSerializerOptions { WriteIndented = true });
            var fileName = $"kye-progress-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
            await JS.InvokeVoidAsync("kyeInterop.downloadTextFile", fileName, json, "application/json");

            _progressStatusIsError = false;
            _progressStatusMessage = $"Progress exported to '{fileName}'.";
        }
        catch (Exception ex)
        {
            _progressStatusIsError = true;
            _progressStatusMessage = $"Progress export failed: {ex.Message}";
        }
    }

    async Task ImportProgressAsync(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file is null)
            return;

        try
        {
            await using var stream = file.OpenReadStream(MaxImportedProgressBytes);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            var imported = JsonSerializer.Deserialize<ProgressStateDto>(json)
                           ?? throw new InvalidOperationException("The selected progress file could not be parsed.");

            _progress = NormalizeProgress(imported);
            await SaveProgressAsync();

            _progressStatusIsError = false;
            _progressStatusMessage = $"Progress imported from '{file.Name}'.";

            if (_levels.Count > 0)
            {
                _level = ResolveInitialLevelIndex();
                _score = 0;
                LoadCurrentLevel();
                StartTimer();
                _shouldFocusBoard = true;
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            _progressStatusIsError = true;
            _progressStatusMessage = $"Progress import failed: {ex.Message}";
        }
    }

    async Task ClearImportedPacksAsync()
    {
        _customPacks.Clear();
        await JS.InvokeVoidAsync("localStorage.removeItem", CustomPackStorageKey);
        _importStatusIsError = false;
        _importStatusMessage = "Imported packs cleared.";
        await ReloadLevelsAsync();
    }

    // ─── Rendering helpers ────────────────────────────────────────────────────
    int BoardPixelWidth => ((Cols + Rows - 1) * TileHalfWidth) + TileWidth + (BoardInsetX * 2);
    int BoardPixelHeight => ((Cols + Rows - 2) * TileHalfHeight) + TileRenderHeight + (BoardInsetY * 2);
    string BoardStyle => $"width:{BoardPixelWidth}px;height:{BoardPixelHeight}px;";
    string CurrentPackName => CurrentLevel?.PackName ?? string.Empty;
    string CurrentTierName => CurrentLevel?.TierName ?? string.Empty;
    int? CurrentBestScore => CurrentLevel is not null && _progress.Completed.TryGetValue(CurrentLevel.Key, out var progress)
        ? progress.BestScore
        : null;

    (int Left, int Top, int ZIndex) ProjectTile(int row, int col)
    {
        var left = BoardInsetX + ((col - row + Rows - 1) * TileHalfWidth);
        var top = BoardInsetY + ((col + row) * TileHalfHeight);
        var zIndex = 10 + ((row + col) * 10);

        return (left, top, zIndex);
    }

    string TileStyle(int row, int col)
    {
        var (left, top, zIndex) = ProjectTile(row, col);

        return $"left:{left}px;top:{top}px;z-index:{zIndex};";
    }

    string ActorStyle(int col, int row, int boost, int hopStamp)
    {
        var (left, top, zIndex) = ProjectTile(row, col);
        return $"left:{left}px;top:{top}px;z-index:{zIndex + boost};--hop-name:{HopAnimationName(hopStamp)};--shadow-name:{ShadowAnimationName(hopStamp)};";
    }

    bool IsLevelCompleted(LevelDefinition level) => _progress.Completed.ContainsKey(level.Key);

    string LevelSelectorLabel(LevelDefinition level)
    {
        var prefix = IsLevelCompleted(level) ? "✓" : "○";
        var source = level.IsCustom ? "Custom" : "Core";
        return $"{prefix} [{source}] {level.TierName} · {level.Name}";
    }

    static string PusherActorClass(Pusher p) => (p.Dx, p.Dy) switch
    {
        (-1, 0) => "actor-pusher-left",
        (1, 0) => "actor-pusher-right",
        (0, -1) => "actor-pusher-up",
        (0, 1) => "actor-pusher-down",
        _ => "actor-pusher-right",
    };

    string FloorClass(int row, int col) => _grid[row, col] switch
    {
        Cell.Wall       => "floor-wall",
        Cell.Soft       => "floor-soft",
        Cell.Diamond    => "floor-gem",
        Cell.Turner     => "floor-turner",
        Cell.AntiTurner => "floor-anti-turner",
        Cell.WormHole   => "floor-wormhole",
        Cell.ExitClosed => "floor-exit-c",
        Cell.ExitOpen   => "floor-exit-o",
        _               => "floor-empty",
    };

    bool HasSolid(int row, int col)
        => _grid[row, col] is Cell.Wall or Cell.Soft;

    string SolidClass(int row, int col) => _grid[row, col] switch
    {
        Cell.Wall => "solid-wall",
        Cell.Soft => "solid-soft",
        _ => string.Empty,
    };

    string SolidStyle(int row, int col)
        => $"--solid-height:{SolidHeight(row, col)}px;";

    string OccluderClass(int row, int col) => _grid[row, col] switch
    {
        Cell.Wall => "occluder-wall",
        Cell.Soft => "occluder-soft",
        _ => string.Empty,
    };

    string OccluderStyle(int row, int col)
    {
        var (left, top, zIndex) = ProjectTile(row, col);
        return $"left:{left}px;top:{top}px;z-index:{zIndex + 7};--solid-height:{SolidHeight(row, col)}px;";
    }

    bool HasObject(int row, int col)
        => _grid[row, col] is Cell.Diamond or Cell.Turner or Cell.AntiTurner or Cell.WormHole or Cell.ExitClosed or Cell.ExitOpen;

    string ObjectClass(int row, int col) => _grid[row, col] switch
    {
        Cell.Diamond    => "object-diamond",
        Cell.Turner     => "object-turner",
        Cell.AntiTurner => "object-anti-turner",
        Cell.WormHole   => "object-wormhole",
        Cell.ExitClosed => "object-exit-c",
        Cell.ExitOpen   => "object-exit-o",
        _               => string.Empty,
    };

    string ObjectContent(int row, int col) => _grid[row, col] switch
    {
        Cell.Diamond    => "◆",
        Cell.Turner     => "↻",
        Cell.AntiTurner => "↺",
        Cell.WormHole   => "◉",
        Cell.ExitClosed => "▣",
        Cell.ExitOpen   => "★",
        _               => string.Empty,
    };

    int SolidHeight(int row, int col)
    {
        var variance = ((row + 1) * 31 + (col + 1) * 17 + (row * col * 7)) & 3;

        return _grid[row, col] switch
        {
            Cell.Wall => 54 + (variance * 6),
            Cell.Soft => 42 + (variance * 4),
            _ => 0,
        };
    }

    static string HopAnimationName(int stamp)
        => stamp <= 0 ? "none" : (stamp & 1) == 0 ? "kye-hop-a" : "kye-hop-b";

    static string ShadowAnimationName(int stamp)
        => stamp <= 0 ? "none" : (stamp & 1) == 0 ? "kye-shadow-a" : "kye-shadow-b";
}

