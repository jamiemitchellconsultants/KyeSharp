using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace KyeSharpWASM.Pages;

public partial class AticAtac : IDisposable
{
    const int RoomCols = 15;
    const int RoomRows = 11;
    const int OpeningCol = RoomCols / 2;
    const int OpeningRow = RoomRows / 2;
    const int InitialLives = 3;

    enum Tile
    {
        Floor,
        Wall,
        DoorBlue,
        DoorGreen,
        DoorRed,
        ExitGateClosed,
        ExitGateOpen,
        KeyBlue,
        KeyGreen,
        KeyRed,
        Relic,
    }

    enum EnemyKind
    {
        Patroller,
        Hunter,
    }

    record struct EnemyState(int X, int Y, int Dx, int Dy, EnemyKind Kind);

    sealed class RoomState
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required Tile[,] Tiles { get; init; }
        public required List<EnemyState> Enemies { get; init; }
        public required int SpawnX { get; init; }
        public required int SpawnY { get; init; }
        public string? LeftRoomId { get; init; }
        public string? RightRoomId { get; init; }
        public string? UpRoomId { get; init; }
        public string? DownRoomId { get; init; }
    }

    readonly Dictionary<string, RoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);

    string _currentRoomId = "foyer";
    int _px;
    int _py;
    int _lives;
    int _tick;
    bool _hasBlueKey;
    bool _hasGreenKey;
    bool _hasRedKey;
    bool _hasRelic;
    bool _gameOver;
    bool _victory;
    bool _shouldFocusBoard;
    string _statusMessage = string.Empty;

    Timer? _timer;
    ElementReference _boardRef;

    RoomState CurrentRoom => _rooms[_currentRoomId];
    string ObjectiveText => _hasRelic
        ? "Return to the foyer and use the opened stair gate to escape."
        : "Find the blue, green, and red keys, then recover the relic from the sanctum.";

    IEnumerable<string> LivesDisplay => Enumerable.Range(0, _lives).Select(_ => "♥");

    protected override void OnInitialized()
    {
        ResetAdventure();
        StartTimer();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_shouldFocusBoard)
        {
            _shouldFocusBoard = false;
            await _boardRef.FocusAsync();
        }
    }

    public void Dispose() => _timer?.Dispose();

    void ResetAdventure()
    {
        _rooms.Clear();
        foreach (var room in BuildRooms())
            _rooms.Add(room.Id, room);

        _currentRoomId = "foyer";
        _lives = InitialLives;
        _tick = 0;
        _hasBlueKey = false;
        _hasGreenKey = false;
        _hasRedKey = false;
        _hasRelic = false;
        _gameOver = false;
        _victory = false;
        _statusMessage = "Explore the manor, avoid the guardians, and recover the relic.";

        PlacePlayer(CurrentRoom.SpawnX, CurrentRoom.SpawnY);
        _shouldFocusBoard = true;
    }

    List<RoomState> BuildRooms()
    {
        return
        [
            CreateRoom(
                id: "foyer",
                name: "Moonlit Foyer",
                leftRoomId: null,
                rightRoomId: "library",
                upRoomId: null,
                downRoomId: "gallery",
                spawnX: 2,
                spawnY: 8,
                decorate: (tiles, enemies) =>
                {
                    AddVerticalWall(tiles, 4, 2, 8, 5);
                    AddVerticalWall(tiles, 10, 2, 8, 5);
                    AddHorizontalWall(tiles, 3, 2, 12, 7);
                    AddHorizontalWall(tiles, 7, 2, 12, 7);
                    tiles[1, 7] = Tile.ExitGateClosed;
                }),

            CreateRoom(
                id: "library",
                name: "Dusty Library",
                leftRoomId: "foyer",
                rightRoomId: "forge",
                upRoomId: null,
                downRoomId: "archive",
                spawnX: 1,
                spawnY: OpeningRow,
                decorate: (tiles, enemies) =>
                {
                    AddHorizontalWall(tiles, 4, 2, 12, 5);
                    AddVerticalWall(tiles, 9, 2, 8, 5);
                    tiles[5, 9] = Tile.DoorGreen;
                    tiles[5, 12] = Tile.KeyRed;
                    enemies.Add(new EnemyState(12, 2, -1, 0, EnemyKind.Patroller));
                }),

            CreateRoom(
                id: "forge",
                name: "Clockwork Forge",
                leftRoomId: "library",
                rightRoomId: null,
                upRoomId: null,
                downRoomId: null,
                spawnX: 1,
                spawnY: OpeningRow,
                decorate: (tiles, enemies) =>
                {
                    AddVerticalWall(tiles, 8, 2, 8, 7);
                    AddHorizontalWall(tiles, 7, 8, 13, 11);
                    tiles[3, 8] = Tile.DoorBlue;
                    tiles[2, 12] = Tile.KeyGreen;
                    enemies.Add(new EnemyState(11, 8, 0, -1, EnemyKind.Patroller));
                }),

            CreateRoom(
                id: "gallery",
                name: "Portrait Gallery",
                leftRoomId: null,
                rightRoomId: "archive",
                upRoomId: "foyer",
                downRoomId: null,
                spawnX: OpeningCol,
                spawnY: 1,
                decorate: (tiles, enemies) =>
                {
                    AddVerticalWall(tiles, 6, 2, 8, 5);
                    AddHorizontalWall(tiles, 7, 4, 12, 6, 10);
                    tiles[3, 11] = Tile.KeyBlue;
                    enemies.Add(new EnemyState(11, 8, 0, 0, EnemyKind.Hunter));
                }),

            CreateRoom(
                id: "archive",
                name: "Whisper Archive",
                leftRoomId: "gallery",
                rightRoomId: "sanctum",
                upRoomId: "library",
                downRoomId: null,
                spawnX: OpeningCol,
                spawnY: 1,
                decorate: (tiles, enemies) =>
                {
                    AddHorizontalWall(tiles, 6, 2, 12, 11);
                    AddVerticalWall(tiles, 11, 2, 8, 5);
                    tiles[5, 11] = Tile.DoorRed;
                    enemies.Add(new EnemyState(4, 3, 0, 0, EnemyKind.Hunter));
                    enemies.Add(new EnemyState(8, 8, -1, 0, EnemyKind.Patroller));
                }),

            CreateRoom(
                id: "sanctum",
                name: "Relic Sanctum",
                leftRoomId: "archive",
                rightRoomId: null,
                upRoomId: null,
                downRoomId: null,
                spawnX: 1,
                spawnY: OpeningRow,
                decorate: (tiles, enemies) =>
                {
                    AddVerticalWall(tiles, 5, 2, 8, 4);
                    AddVerticalWall(tiles, 9, 2, 8, 6);
                    AddHorizontalWall(tiles, 5, 5, 9, 7);
                    tiles[5, 7] = Tile.Relic;
                    enemies.Add(new EnemyState(3, 3, 0, 0, EnemyKind.Hunter));
                    enemies.Add(new EnemyState(11, 7, 0, -1, EnemyKind.Patroller));
                }),
        ];
    }

    RoomState CreateRoom(
        string id,
        string name,
        string? leftRoomId,
        string? rightRoomId,
        string? upRoomId,
        string? downRoomId,
        int spawnX,
        int spawnY,
        Action<Tile[,], List<EnemyState>> decorate)
    {
        var tiles = new Tile[RoomRows, RoomCols];
        var enemies = new List<EnemyState>();

        for (int row = 0; row < RoomRows; row++)
        {
            for (int col = 0; col < RoomCols; col++)
            {
                tiles[row, col] = row == 0 || row == RoomRows - 1 || col == 0 || col == RoomCols - 1
                    ? Tile.Wall
                    : Tile.Floor;
            }
        }

        if (leftRoomId is not null)
        {
            tiles[OpeningRow, 0] = Tile.Floor;
            tiles[OpeningRow, 1] = Tile.Floor;
        }

        if (rightRoomId is not null)
        {
            tiles[OpeningRow, RoomCols - 1] = Tile.Floor;
            tiles[OpeningRow, RoomCols - 2] = Tile.Floor;
        }

        if (upRoomId is not null)
        {
            tiles[0, OpeningCol] = Tile.Floor;
            tiles[1, OpeningCol] = Tile.Floor;
        }

        if (downRoomId is not null)
        {
            tiles[RoomRows - 1, OpeningCol] = Tile.Floor;
            tiles[RoomRows - 2, OpeningCol] = Tile.Floor;
        }

        decorate(tiles, enemies);

        return new RoomState
        {
            Id = id,
            Name = name,
            Tiles = tiles,
            Enemies = enemies,
            SpawnX = spawnX,
            SpawnY = spawnY,
            LeftRoomId = leftRoomId,
            RightRoomId = rightRoomId,
            UpRoomId = upRoomId,
            DownRoomId = downRoomId,
        };
    }

    static void AddHorizontalWall(Tile[,] tiles, int y, int fromX, int toX, params int[] gaps)
    {
        var gapSet = new HashSet<int>(gaps);
        for (int x = fromX; x <= toX; x++)
        {
            if (!gapSet.Contains(x))
                tiles[y, x] = Tile.Wall;
        }
    }

    static void AddVerticalWall(Tile[,] tiles, int x, int fromY, int toY, params int[] gaps)
    {
        var gapSet = new HashSet<int>(gaps);
        for (int y = fromY; y <= toY; y++)
        {
            if (!gapSet.Contains(y))
                tiles[y, x] = Tile.Wall;
        }
    }

    void StartTimer()
    {
        _timer?.Dispose();
        _timer = new Timer(_ =>
        {
            InvokeAsync(() =>
            {
                if (_gameOver || _victory)
                    return;

                Tick();
                StateHasChanged();
            });
        }, null, 500, 230);
    }

    void Tick()
    {
        _tick++;
        MoveEnemies();
    }

    void MoveEnemies()
    {
        var room = CurrentRoom;

        for (int i = 0; i < room.Enemies.Count; i++)
        {
            var enemy = room.Enemies[i];
            EnemyState updated = enemy;

            if (enemy.Kind == EnemyKind.Patroller)
            {
                updated = MovePatroller(room, enemy, i);
            }
            else if ((_tick & 1) == 0)
            {
                updated = MoveHunter(room, enemy, i);
            }

            room.Enemies[i] = updated;
            if (updated.X == _px && updated.Y == _py)
            {
                HandlePlayerHit($"A {(updated.Kind == EnemyKind.Hunter ? "stalker" : "sentinel")} caught you.");
                return;
            }
        }
    }

    EnemyState MovePatroller(RoomState room, EnemyState enemy, int enemyIndex)
    {
        var nx = enemy.X + enemy.Dx;
        var ny = enemy.Y + enemy.Dy;
        if (CanEnemyOccupy(room, nx, ny, enemyIndex))
            return enemy with { X = nx, Y = ny };

        var reversedDx = -enemy.Dx;
        var reversedDy = -enemy.Dy;
        nx = enemy.X + reversedDx;
        ny = enemy.Y + reversedDy;
        if (CanEnemyOccupy(room, nx, ny, enemyIndex))
            return enemy with { X = nx, Y = ny, Dx = reversedDx, Dy = reversedDy };

        return enemy with { Dx = reversedDx, Dy = reversedDy };
    }

    EnemyState MoveHunter(RoomState room, EnemyState enemy, int enemyIndex)
    {
        var dxToPlayer = _px - enemy.X;
        var dyToPlayer = _py - enemy.Y;

        var attempts = new List<(int Dx, int Dy)>(4);
        if (Math.Abs(dxToPlayer) >= Math.Abs(dyToPlayer))
        {
            attempts.Add((Math.Sign(dxToPlayer), 0));
            attempts.Add((0, Math.Sign(dyToPlayer)));
        }
        else
        {
            attempts.Add((0, Math.Sign(dyToPlayer)));
            attempts.Add((Math.Sign(dxToPlayer), 0));
        }

        attempts.Add((-Math.Sign(dxToPlayer), 0));
        attempts.Add((0, -Math.Sign(dyToPlayer)));

        foreach (var (dx, dy) in attempts)
        {
            if (dx == 0 && dy == 0)
                continue;

            var nx = enemy.X + dx;
            var ny = enemy.Y + dy;
            if (CanEnemyOccupy(room, nx, ny, enemyIndex))
                return enemy with { X = nx, Y = ny, Dx = dx, Dy = dy };
        }

        return enemy;
    }

    bool CanEnemyOccupy(RoomState room, int x, int y, int enemyIndex)
    {
        if (x < 0 || x >= RoomCols || y < 0 || y >= RoomRows)
            return false;

        var tile = room.Tiles[y, x];
        if (tile is Tile.Wall or Tile.DoorBlue or Tile.DoorGreen or Tile.DoorRed or Tile.ExitGateClosed)
            return false;

        for (int i = 0; i < room.Enemies.Count; i++)
        {
            if (i == enemyIndex)
                continue;
            if (room.Enemies[i].X == x && room.Enemies[i].Y == y)
                return false;
        }

        return true;
    }

    void TryMove(int dx, int dy)
    {
        if (_gameOver || _victory)
            return;

        if (TryTransitionRoom(dx, dy))
        {
            _shouldFocusBoard = true;
            return;
        }

        var nx = _px + dx;
        var ny = _py + dy;
        if (nx < 0 || nx >= RoomCols || ny < 0 || ny >= RoomRows)
            return;

        var room = CurrentRoom;
        var tile = room.Tiles[ny, nx];

        if (!CanPlayerEnter(room, nx, ny, ref tile))
        {
            _shouldFocusBoard = true;
            return;
        }

        if (room.Enemies.Any(enemy => enemy.X == nx && enemy.Y == ny))
        {
            HandlePlayerHit("You ran into a roaming guardian.");
            _shouldFocusBoard = true;
            return;
        }

        PlacePlayer(nx, ny);
        ResolveTile(room, nx, ny);
        _shouldFocusBoard = true;
    }

    bool CanPlayerEnter(RoomState room, int x, int y, ref Tile tile)
    {
        switch (tile)
        {
            case Tile.Wall:
                return false;

            case Tile.DoorBlue:
                if (!_hasBlueKey)
                {
                    _statusMessage = "The cobalt door needs the blue key.";
                    return false;
                }
                room.Tiles[y, x] = Tile.Floor;
                tile = Tile.Floor;
                _statusMessage = "The blue key clicks the cobalt door open.";
                return true;

            case Tile.DoorGreen:
                if (!_hasGreenKey)
                {
                    _statusMessage = "The verdant door needs the green key.";
                    return false;
                }
                room.Tiles[y, x] = Tile.Floor;
                tile = Tile.Floor;
                _statusMessage = "The green key unlocks the verdant door.";
                return true;

            case Tile.DoorRed:
                if (!_hasRedKey)
                {
                    _statusMessage = "The crimson door needs the red key.";
                    return false;
                }
                room.Tiles[y, x] = Tile.Floor;
                tile = Tile.Floor;
                _statusMessage = "The red key unlocks the crimson door.";
                return true;

            case Tile.ExitGateClosed:
                _statusMessage = _hasRelic
                    ? "The stair gate rattles but stays shut."
                    : "The stair gate is sealed. Recover the relic first.";
                return false;

            default:
                return true;
        }
    }

    void ResolveTile(RoomState room, int x, int y)
    {
        switch (room.Tiles[y, x])
        {
            case Tile.KeyBlue:
                _hasBlueKey = true;
                room.Tiles[y, x] = Tile.Floor;
                _statusMessage = "You found the blue key.";
                break;

            case Tile.KeyGreen:
                _hasGreenKey = true;
                room.Tiles[y, x] = Tile.Floor;
                _statusMessage = "You found the green key.";
                break;

            case Tile.KeyRed:
                _hasRedKey = true;
                room.Tiles[y, x] = Tile.Floor;
                _statusMessage = "You found the red key.";
                break;

            case Tile.Relic:
                _hasRelic = true;
                room.Tiles[y, x] = Tile.Floor;
                OpenFoyerGate();
                _statusMessage = "You claimed the relic. Escape through the foyer stair gate!";
                break;

            case Tile.ExitGateOpen:
                _victory = true;
                _statusMessage = "You escaped the haunted manor with the relic.";
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                break;

            default:
                if (string.IsNullOrWhiteSpace(_statusMessage) || _statusMessage.Contains("Entered", StringComparison.Ordinal))
                    _statusMessage = ObjectiveText;
                break;
        }
    }

    void OpenFoyerGate()
    {
        var foyer = _rooms["foyer"];
        for (int row = 0; row < RoomRows; row++)
        {
            for (int col = 0; col < RoomCols; col++)
            {
                if (foyer.Tiles[row, col] == Tile.ExitGateClosed)
                    foyer.Tiles[row, col] = Tile.ExitGateOpen;
            }
        }
    }

    bool TryTransitionRoom(int dx, int dy)
    {
        var room = CurrentRoom;
        string? destinationRoomId = null;
        int nextX = _px;
        int nextY = _py;

        if (dx == -1 && _px == 0 && _py == OpeningRow)
        {
            destinationRoomId = room.LeftRoomId;
            nextX = RoomCols - 1;
            nextY = OpeningRow;
        }
        else if (dx == 1 && _px == RoomCols - 1 && _py == OpeningRow)
        {
            destinationRoomId = room.RightRoomId;
            nextX = 0;
            nextY = OpeningRow;
        }
        else if (dy == -1 && _py == 0 && _px == OpeningCol)
        {
            destinationRoomId = room.UpRoomId;
            nextX = OpeningCol;
            nextY = RoomRows - 1;
        }
        else if (dy == 1 && _py == RoomRows - 1 && _px == OpeningCol)
        {
            destinationRoomId = room.DownRoomId;
            nextX = OpeningCol;
            nextY = 0;
        }

        if (destinationRoomId is null)
            return false;

        _currentRoomId = destinationRoomId;
        PlacePlayer(nextX, nextY);
        _statusMessage = $"Entered {CurrentRoom.Name}.";

        if (CurrentRoom.Enemies.Any(enemy => enemy.X == _px && enemy.Y == _py))
            HandlePlayerHit("Something was waiting in the doorway.");

        return true;
    }

    void HandlePlayerHit(string message)
    {
        _lives--;
        if (_lives <= 0)
        {
            _lives = 0;
            _gameOver = true;
            _statusMessage = $"{message} The manor has claimed you.";
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        _currentRoomId = "foyer";
        PlacePlayer(CurrentRoom.SpawnX, CurrentRoom.SpawnY);
        _statusMessage = $"{message} You retreat to the foyer. {ObjectiveText}";
    }

    void PlacePlayer(int x, int y)
    {
        _px = x;
        _py = y;
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
                MoveLeft();
                break;

            case "ArrowUp":
            case "w":
            case "W":
            case "e":
            case "E":
                MoveUp();
                break;

            case "ArrowDown":
            case "s":
            case "S":
            case "z":
            case "Z":
                MoveDown();
                break;

            case "ArrowRight":
            case "d":
            case "D":
            case "c":
            case "C":
                MoveRight();
                break;

            case "r":
            case "R":
                RestartAdventure();
                break;
        }
    }

    void MoveLeft() => TryMove(-1, 0);
    void MoveUp() => TryMove(0, -1);
    void MoveDown() => TryMove(0, 1);
    void MoveRight() => TryMove(1, 0);

    void RestartAdventure()
    {
        ResetAdventure();
        StartTimer();
    }

    bool IsPlayerAt(int row, int col) => _py == row && _px == col;

    EnemyState? EnemyAt(int row, int col)
    {
        foreach (var enemy in CurrentRoom.Enemies)
        {
            if (enemy.X == col && enemy.Y == row)
                return enemy;
        }

        return null;
    }

    string CellClass(int row, int col)
    {
        if (IsPlayerAt(row, col))
            return "cell-player";

        var enemy = EnemyAt(row, col);
        if (enemy is not null)
            return enemy.Value.Kind == EnemyKind.Hunter ? "cell-hunter" : "cell-patroller";

        return CurrentRoom.Tiles[row, col] switch
        {
            Tile.Wall => "cell-wall",
            Tile.DoorBlue => "cell-door-blue",
            Tile.DoorGreen => "cell-door-green",
            Tile.DoorRed => "cell-door-red",
            Tile.ExitGateClosed => "cell-exit-closed",
            Tile.ExitGateOpen => "cell-exit-open",
            Tile.KeyBlue => "cell-key-blue",
            Tile.KeyGreen => "cell-key-green",
            Tile.KeyRed => "cell-key-red",
            Tile.Relic => "cell-relic",
            _ => "cell-floor",
        };
    }

    string CellGlyph(int row, int col)
    {
        if (IsPlayerAt(row, col))
            return "◉";

        var enemy = EnemyAt(row, col);
        if (enemy is not null)
            return enemy.Value.Kind == EnemyKind.Hunter ? "☠" : "✥";

        return CurrentRoom.Tiles[row, col] switch
        {
            Tile.DoorBlue => "▥",
            Tile.DoorGreen => "▥",
            Tile.DoorRed => "▥",
            Tile.ExitGateClosed => "⇪",
            Tile.ExitGateOpen => "⇧",
            Tile.KeyBlue => "✦",
            Tile.KeyGreen => "✦",
            Tile.KeyRed => "✦",
            Tile.Relic => "✹",
            _ => string.Empty,
        };
    }

    string KeyBadgeClass(bool owned) => owned ? "owned" : "missing";
}

