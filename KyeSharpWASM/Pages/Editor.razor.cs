using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace KyeSharpWASM.Pages;

public partial class Editor
{
    const int Cols = 20;
    const int Rows = 15;
    const int MaxImportBytes = 256_000;
    const int MaxHistory = 100;
    const string PreviewLevelStorageKey = "kye.preview-level.v1";
    const string PreviewAutoSelectKey = "kye.preview-level-select.v1";

    enum ToolMode
    {
        Paint,
        Rectangle,
        Line,
    }

    sealed class LevelFileDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Hint { get; set; }
        public string[]? Map { get; set; }
    }

    sealed class TileDef
    {
        public required char Symbol { get; init; }
        public required string Label { get; init; }
    }

    static readonly TileDef[] Tiles =
    [
        new() { Symbol = '.', Label = "Empty" },
        new() { Symbol = '#', Label = "Wall" },
        new() { Symbol = 's', Label = "Soft Wall" },
        new() { Symbol = '*', Label = "Diamond" },
        new() { Symbol = 'E', Label = "Exit" },
        new() { Symbol = 'P', Label = "Player" },
        new() { Symbol = 'r', Label = "Roundel" },
        new() { Symbol = 'b', Label = "Rocky Horizontal" },
        new() { Symbol = 'v', Label = "Rocky Vertical" },
        new() { Symbol = 't', Label = "Clockwise Turner" },
        new() { Symbol = 'a', Label = "Anti-Clockwise Turner" },
        new() { Symbol = 'w', Label = "Worm Hole" },
        new() { Symbol = 'L', Label = "Left Pusher" },
        new() { Symbol = 'R', Label = "Right Pusher" },
        new() { Symbol = 'U', Label = "Up Pusher" },
        new() { Symbol = 'D', Label = "Down Pusher" },
    ];

    [Inject]
    IJSRuntime Js { get; set; } = default!;

    [Inject]
    NavigationManager Nav { get; set; } = default!;

    readonly char[,] _grid = new char[Rows, Cols];
    char _selectedTile = '#';
    bool _isPainting;
    ToolMode _toolMode = ToolMode.Paint;
    int? _anchorRow;
    int? _anchorCol;
    int _hoverRow;
    int _hoverCol;
    readonly HashSet<(int Row, int Col)> _previewCells = [];

    readonly Stack<string[]> _undoStack = new();
    readonly Stack<string[]> _redoStack = new();

    string _levelId = "new-level";
    string _levelName = "New Level";
    string _levelHint = string.Empty;

    string _statusMessage = "Ready.";
    bool _statusIsError;

    string PreviewJson => JsonSerializer.Serialize(new LevelFileDto
    {
        Id = _levelId,
        Name = _levelName,
        Hint = _levelHint,
        Map = ToMapLines(),
    }, new JsonSerializerOptions { WriteIndented = true });

    protected override void OnInitialized()
    {
        FillEmptyCore();
        ApplyBorderWallsCore();
        EnsureSingle('P', 1, 1);
        EnsureSingle('E', Cols - 2, Rows - 2);
        _hoverRow = 1;
        _hoverCol = 1;
        SetStatus("Ready. Use Paint/Rectangle/Line tools to build your map.", false);
    }

    void SelectTile(char symbol) => _selectedTile = symbol;

    void SelectTool(ToolMode tool)
    {
        _toolMode = tool;
        ClearPreview();
    }

    void Undo()
    {
        if (_undoStack.Count == 0)
        {
            SetStatus("Nothing to undo.", true);
            return;
        }

        _redoStack.Push(ToMapLines());
        RestoreSnapshot(_undoStack.Pop());
        SetStatus("Undo applied.", false);
    }

    void Redo()
    {
        if (_redoStack.Count == 0)
        {
            SetStatus("Nothing to redo.", true);
            return;
        }

        _undoStack.Push(ToMapLines());
        RestoreSnapshot(_redoStack.Pop());
        SetStatus("Redo applied.", false);
    }

    void BeginPaint(int row, int col)
    {
        _isPainting = true;
        _hoverRow = row;
        _hoverCol = col;
        _anchorRow = row;
        _anchorCol = col;

        if (_toolMode == ToolMode.Paint)
        {
            ClearPreview();
            PushUndoSnapshot();
            _redoStack.Clear();
            PaintCell(row, col);
        }
        else
        {
            UpdatePreviewFromDrag(_anchorRow ?? row, _anchorCol ?? col, row, col);
        }
    }

    void PaintOver(int row, int col)
    {
        _hoverRow = row;
        _hoverCol = col;

        if (_isPainting)
        {
            if (_toolMode == ToolMode.Paint)
                PaintCell(row, col);
            else
                UpdatePreviewFromDrag(_anchorRow ?? row, _anchorCol ?? col, row, col);
        }
    }

    void EndPaint()
    {
        if (!_isPainting)
            return;

        if (_toolMode is ToolMode.Rectangle or ToolMode.Line)
        {
            var startRow = _anchorRow ?? _hoverRow;
            var startCol = _anchorCol ?? _hoverCol;
            var endRow = _hoverRow;
            var endCol = _hoverCol;

            PushUndoSnapshot();
            _redoStack.Clear();

            if (_toolMode == ToolMode.Rectangle)
                PaintRectangle(startRow, startCol, endRow, endCol);
            else
                PaintLine(startRow, startCol, endRow, endCol);
        }

        _isPainting = false;
        _anchorRow = null;
        _anchorCol = null;
        ClearPreview();
    }

    void UpdatePreviewFromDrag(int startRow, int startCol, int endRow, int endCol)
    {
        _previewCells.Clear();

        IEnumerable<(int Row, int Col)> points = _toolMode switch
        {
            ToolMode.Rectangle => GetRectanglePoints(startRow, startCol, endRow, endCol),
            ToolMode.Line => GetLinePoints(startRow, startCol, endRow, endCol),
            _ => []
        };

        foreach (var point in points)
            _previewCells.Add(point);
    }

    void ClearPreview() => _previewCells.Clear();

    IEnumerable<(int Row, int Col)> GetRectanglePoints(int startRow, int startCol, int endRow, int endCol)
    {
        var minRow = Math.Min(startRow, endRow);
        var maxRow = Math.Max(startRow, endRow);
        var minCol = Math.Min(startCol, endCol);
        var maxCol = Math.Max(startCol, endCol);

        for (var row = minRow; row <= maxRow; row++)
            for (var col = minCol; col <= maxCol; col++)
                if (InBounds(row, col))
                    yield return (row, col);
    }

    IEnumerable<(int Row, int Col)> GetLinePoints(int startRow, int startCol, int endRow, int endCol)
    {
        var x0 = startCol;
        var y0 = startRow;
        var x1 = endCol;
        var y1 = endRow;

        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (InBounds(y0, x0))
                yield return (y0, x0);

            if (x0 == x1 && y0 == y1)
                break;

            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    void PaintCell(int row, int col)
    {
        if (!InBounds(row, col))
            return;

        var tile = _selectedTile;
        if (!IsAllowedTile(tile))
            return;

        if (tile == 'P')
            RemoveAll('P');

        if (tile == 'E')
            RemoveAll('E');

        _grid[row, col] = tile;
    }

    void FillEmpty()
    {
        ApplyEdit(FillEmptyCore, "Map cleared.");
    }

    void ApplyBorderWalls()
    {
        ApplyEdit(ApplyBorderWallsCore, "Border walls added.");
    }

    void FillEmptyCore()
    {
        for (var row = 0; row < Rows; row++)
            for (var col = 0; col < Cols; col++)
                _grid[row, col] = '.';
    }

    void ApplyBorderWallsCore()
    {
        for (var row = 0; row < Rows; row++)
            for (var col = 0; col < Cols; col++)
                if (row == 0 || row == Rows - 1 || col == 0 || col == Cols - 1)
                    _grid[row, col] = '#';
    }

    void ApplyBasicTemplate()
    {
        ApplyEdit(() =>
        {
            FillEmptyCore();
            ApplyBorderWallsCore();

            for (var row = 4; row <= 10; row++)
                _grid[row, 6] = '#';

            for (var col = 10; col <= 14; col++)
                _grid[7, col] = '#';

            _grid[3, 3] = '*';
            _grid[5, 12] = '*';
            _grid[10, 15] = 'r';

            EnsureSingle('P', 2, 2);
            EnsureSingle('E', Cols - 3, Rows - 3);
        }, "Basic template applied.");
    }

    void ApplyEdit(Action edit, string successMessage)
    {
        PushUndoSnapshot();
        _redoStack.Clear();
        edit();
        SetStatus(successMessage, false);
    }

    void PushUndoSnapshot()
    {
        _undoStack.Push(ToMapLines());
        while (_undoStack.Count > MaxHistory)
        {
            var snapshots = _undoStack.Reverse().Take(MaxHistory).Reverse().ToArray();
            _undoStack.Clear();
            foreach (var snapshot in snapshots)
                _undoStack.Push(snapshot);
        }
    }

    void ValidateMap()
    {
        var result = ValidateCurrentMap();
        SetStatus(result.Ok ? "Map is valid." : result.ErrorMessage, !result.Ok);
    }

    async Task ExportLevelAsync()
    {
        var result = ValidateCurrentMap();
        if (!result.Ok)
        {
            SetStatus(result.ErrorMessage, true);
            return;
        }

        var dto = new LevelFileDto
        {
            Id = string.IsNullOrWhiteSpace(_levelId) ? "new-level" : _levelId.Trim(),
            Name = string.IsNullOrWhiteSpace(_levelName) ? "New Level" : _levelName.Trim(),
            Hint = _levelHint.Trim(),
            Map = ToMapLines(),
        };

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        var fileName = $"{dto.Id}.json";

        await Js.InvokeVoidAsync("kyeInterop.downloadTextFile", fileName, json, "application/json");
        SetStatus($"Exported {fileName}.", false);
    }

    async Task ImportLevelAsync(InputFileChangeEventArgs e)
    {
        try
        {
            var file = e.File;
            await using var stream = file.OpenReadStream(MaxImportBytes);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var dto = JsonSerializer.Deserialize<LevelFileDto>(json)
                      ?? throw new InvalidOperationException("Could not parse JSON level file.");

            if (dto.Map is null)
                throw new InvalidOperationException("JSON level file is missing map.");

            ValidateMapLines(dto.Map);

            _levelId = string.IsNullOrWhiteSpace(dto.Id) ? "imported-level" : dto.Id.Trim();
            _levelName = string.IsNullOrWhiteSpace(dto.Name) ? "Imported Level" : dto.Name.Trim();
            _levelHint = dto.Hint?.Trim() ?? string.Empty;

            PushUndoSnapshot();
            _redoStack.Clear();
            LoadMap(dto.Map);
            SetStatus($"Imported {file.Name}.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Import failed: {ex.Message}", true);
        }
    }

    async Task PlayThisLevelAsync()
    {
        var result = ValidateCurrentMap();
        if (!result.Ok)
        {
            SetStatus(result.ErrorMessage, true);
            return;
        }

        var dto = new LevelFileDto
        {
            Id = string.IsNullOrWhiteSpace(_levelId) ? "editor-level" : _levelId.Trim(),
            Name = string.IsNullOrWhiteSpace(_levelName) ? "Editor Level" : _levelName.Trim(),
            Hint = _levelHint.Trim(),
            Map = ToMapLines(),
        };

        var previewJson = JsonSerializer.Serialize(dto);
        var levelKey = $"editor-preview:{dto.Id}";

        await Js.InvokeVoidAsync("localStorage.setItem", PreviewLevelStorageKey, previewJson);
        await Js.InvokeVoidAsync("localStorage.setItem", PreviewAutoSelectKey, levelKey);

        Nav.NavigateTo("/kye");
    }

    (bool Ok, string ErrorMessage) ValidateCurrentMap()
    {
        try
        {
            ValidateMapLines(ToMapLines());
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    static void ValidateMapLines(string[] map)
    {
        if (map.Length != Rows)
            throw new InvalidOperationException($"Map must contain exactly {Rows} rows.");

        var playerCount = 0;
        var exitCount = 0;

        for (var row = 0; row < Rows; row++)
        {
            var line = map[row];
            if (line.Length != Cols)
                throw new InvalidOperationException($"Row {row + 1} must be exactly {Cols} characters.");

            foreach (var ch in line)
            {
                if (!IsAllowedTile(ch))
                    throw new InvalidOperationException($"Unsupported symbol '{ch}'.");

                if (ch == 'P') playerCount++;
                if (ch == 'E') exitCount++;
            }
        }

        if (playerCount != 1)
            throw new InvalidOperationException("Map must contain exactly one 'P' (player).");

        if (exitCount != 1)
            throw new InvalidOperationException("Map must contain exactly one 'E' (exit).");
    }

    string[] ToMapLines()
    {
        var map = new string[Rows];
        for (var row = 0; row < Rows; row++)
        {
            var chars = new char[Cols];
            for (var col = 0; col < Cols; col++)
                chars[col] = _grid[row, col];
            map[row] = new string(chars);
        }

        return map;
    }

    void LoadMap(string[] map)
    {
        for (var row = 0; row < Rows; row++)
            for (var col = 0; col < Cols; col++)
                _grid[row, col] = map[row][col];
    }

    void RestoreSnapshot(string[] snapshot) => LoadMap(snapshot);

    void PaintRectangle(int startRow, int startCol, int endRow, int endCol)
    {
        var minRow = Math.Min(startRow, endRow);
        var maxRow = Math.Max(startRow, endRow);
        var minCol = Math.Min(startCol, endCol);
        var maxCol = Math.Max(startCol, endCol);

        for (var row = minRow; row <= maxRow; row++)
            for (var col = minCol; col <= maxCol; col++)
                PaintCell(row, col);
    }

    void PaintLine(int startRow, int startCol, int endRow, int endCol)
    {
        var x0 = startCol;
        var y0 = startRow;
        var x1 = endCol;
        var y1 = endRow;

        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            PaintCell(y0, x0);

            if (x0 == x1 && y0 == y1)
                break;

            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    void RemoveAll(char symbol)
    {
        for (var row = 0; row < Rows; row++)
            for (var col = 0; col < Cols; col++)
                if (_grid[row, col] == symbol)
                    _grid[row, col] = '.';
    }

    void EnsureSingle(char symbol, int col, int row)
    {
        RemoveAll(symbol);
        if (InBounds(row, col))
            _grid[row, col] = symbol;
    }

    static bool IsAllowedTile(char ch)
        => ch is '#' or '.' or '*' or 'E' or 'P' or 'r' or 'b' or 'v' or 's' or 't' or 'a' or 'w' or 'L' or 'R' or 'U' or 'D';

    static bool InBounds(int row, int col)
        => row >= 0 && row < Rows && col >= 0 && col < Cols;

    static string CellClass(char ch) => ch switch
    {
        '#' => "c-wall",
        's' => "c-soft",
        '*' => "c-gem",
        't' => "c-turner",
        'a' => "c-anti-turner",
        'w' => "c-wormhole",
        'E' => "c-exit",
        'P' => "c-player",
        'r' => "c-roundel",
        'b' => "c-rocky",
        'v' => "c-rocky",
        'L' => "c-pusher",
        'R' => "c-pusher",
        'U' => "c-pusher",
        'D' => "c-pusher",
        _ => "c-empty",
    };

    static string CellGlyph(char ch) => ch switch
    {
        '.' => string.Empty,
        '*' => "◆",
        't' => "↻",
        'a' => "↺",
        'w' => "◉",
        'L' => "←",
        'R' => "→",
        'U' => "↑",
        'D' => "↓",
        _ => ch.ToString(),
    };

    bool IsPreviewCell(int row, int col) => _previewCells.Contains((row, col));

    char DisplayTile(int row, int col)
        => IsPreviewCell(row, col) ? _selectedTile : _grid[row, col];

    void SetStatus(string message, bool isError)
    {
        _statusMessage = message;
        _statusIsError = isError;
    }
}

