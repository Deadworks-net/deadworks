using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace TicTacToePlugin;

/// <summary>
/// Two-player tic tac toe on a server-driven panel. The layout is built in C#
/// and re-sent whenever the board changes, so players need nothing installed.
/// </summary>
public class TicTacToePlugin : DeadworksPluginBase {
	public override string Name => "TicTacToe";

	private const string PanelId = "ttt";
	private const char None = ' ';

	private const string Accent = "#E6685F";
	private const string XColour = "#FF8A7A";
	private const string OColour = "#FFEFD7";

	private static readonly int[][] WinningLines = {
		new[] { 0, 1, 2 }, new[] { 3, 4, 5 }, new[] { 6, 7, 8 },
		new[] { 0, 3, 6 }, new[] { 1, 4, 7 }, new[] { 2, 5, 8 },
		new[] { 0, 4, 8 }, new[] { 2, 4, 6 },
	};

	private readonly char[] _cells = new char[9];
	private readonly HashSet<int> _viewers = new();
	private int _slotX = -1;
	private int _slotO = -1;
	private char _turn = 'X';
	private char _winner = None;
	private bool _drawn;
	private int[] _winningLine = Array.Empty<int>();
	private int _scoreX;
	private int _scoreO;
	private char _opensNextRound = 'X';

	public override void OnLoad(bool isReload) {
		ClearBoard();

		UI.Panel(PanelId).On("move", e => {
			if (TryPlay(e.Caller.Slot, e.ArgAt(0))) Redraw();
		});

		UI.Panel(PanelId).On("restart", e => {
			if (SideOf(e.Caller.Slot) == None) return;
			StartNextRound();
			Redraw();
		});

		Console.WriteLine($"[{Name}] loaded — /ttt to play");
	}

	public override void OnClientDisconnect(ClientDisconnectedEvent args) => Leave(args.Slot);

	[Command("ttt", Description = "Toggle the tic tac toe board. The first two callers get the seats.")]
	public void CmdTicTacToe(CCitadelPlayerController player) {
		string outcome = _viewers.Contains(player.Slot) ? Close(player) : Open(player);
		Console.WriteLine($"[{Name}] slot {player.Slot}: {outcome}");
	}

	private string Close(CCitadelPlayerController player) {
		UI.Panel(PanelId).DestroyLayout(player.Recipients);
		Leave(player.Slot);
		return "closed";
	}

	private string Open(CCitadelPlayerController player) {
		_viewers.Add(player.Slot);
		char side = TakeSeat(player.Slot);
		Redraw();
		return side == None ? "spectating" : $"seated as {side}";
	}

	private void Leave(int slot) {
		_viewers.Remove(slot);
		if (slot != _slotX && slot != _slotO) return;

		if (slot == _slotX) _slotX = -1; else _slotO = -1;
		_scoreX = 0;
		_scoreO = 0;
		ClearBoard();
		Redraw();
	}

	private char TakeSeat(int slot) {
		char seated = SideOf(slot);
		if (seated != None) return seated;
		if (_slotX < 0) { _slotX = slot; return 'X'; }
		if (_slotO < 0) { _slotO = slot; return 'O'; }
		return None;
	}

	// ─── rules ─────────────────────────────────────────────────────────────

	private char SideOf(int slot) =>
		slot < 0 ? None : slot == _slotX ? 'X' : slot == _slotO ? 'O' : None;

	private static char Opponent(char side) => side == 'X' ? 'O' : 'X';

	private bool BothSeated => _slotX >= 0 && _slotO >= 0;

	private bool Finished => _winner != None || _drawn;

	private bool BoardFull => Array.IndexOf(_cells, None) < 0;

	private bool CanPlay(char side, int cell) =>
		!Finished && BothSeated && side == _turn && _cells[cell] == None;

	private bool TryPlay(int slot, string arg) {
		char side = SideOf(slot);
		if (side == None) return false;
		if (!int.TryParse(arg, out int cell) || cell < 0 || cell > 8) return false;
		if (!CanPlay(side, cell)) return false;

		_cells[cell] = side;

		if (TryCompleteLine(side, out _winningLine)) {
			_winner = side;
			if (side == 'X') _scoreX++; else _scoreO++;
		} else if (BoardFull) {
			_drawn = true;
		} else {
			_turn = Opponent(side);
		}
		return true;
	}

	private bool TryCompleteLine(char side, out int[] line) {
		foreach (var candidate in WinningLines) {
			if (_cells[candidate[0]] == side && _cells[candidate[1]] == side && _cells[candidate[2]] == side) {
				line = candidate;
				return true;
			}
		}
		line = Array.Empty<int>();
		return false;
	}

	private bool IsWinningCell(int cell) => Array.IndexOf(_winningLine, cell) >= 0;

	private void ClearBoard() {
		Array.Fill(_cells, None);
		_winner = None;
		_drawn = false;
		_winningLine = Array.Empty<int>();
		_turn = _opensNextRound;
	}

	private void StartNextRound() {
		_opensNextRound = Opponent(_opensNextRound);
		ClearBoard();
	}

	private string StatusFor(char viewer) {
		bool spectating = viewer == None;
		if (_drawn) return "Draw";
		if (_winner != None)
			return spectating ? $"{_winner} wins" : viewer == _winner ? "You win" : "You lose";
		if (!BothSeated) return "Waiting for an opponent";
		return spectating ? $"{_turn} to play" : viewer == _turn ? "Your turn" : "Their turn";
	}

	// ─── layout ────────────────────────────────────────────────────────────

	private static readonly (string, string)[] Card = {
		("horizontal-align", "center"), ("vertical-align", "center"),
		("width", "288px"), ("background-color", "#140b09fa"),
		("border", $"1px solid {Accent}44"), ("border-radius", "5px"),
	};

	private static readonly (string, string)[] CellSize = {
		("width", "72px"), ("height", "72px"), ("margin", "2px"), ("border-radius", "3px"),
	};

	private static readonly (string, string)[] MarkText = {
		("width", "100%"), ("text-align", "center"),
		("vertical-align", "center"), ("font-size", "42px"),
	};

	private static readonly (string, string)[] WinningCell = {
		("background-color", $"{Accent}33"), ("border", $"1px solid {Accent}AA"),
	};

	private static readonly (string, string)[] TakenCell = {
		("background-color", "#FFEFD705"), ("border", "1px solid #FFEFD70A"),
	};

	private static readonly (string, string)[] PlayableCell = {
		("background-color", "#FFEFD70A"), ("border", $"1px solid {Accent}66"),
	};

	private static readonly (string, string)[] IdleCell = {
		("background-color", "#FFEFD70A"), ("border", "1px solid #FFEFD714"),
	};

	private static readonly (string, string)[] HoveredCell = {
		("background-color", $"{Accent}30"), ("border", $"1px solid {Accent}CC"),
	};

	private static readonly (string, string)[] PressedCell = {
		("background-color", $"{Accent}66"),
	};

	private void Redraw() {
		foreach (int slot in _viewers) DrawFor(slot);
	}

	private void DrawFor(int slot) {
		var player = Players.FromSlot(slot);
		if (player == null) return;
		UI.Panel(PanelId).BuildLayout(player.Recipients, Board(SideOf(slot)));
	}

	private UINode Board(char viewer) {
		var card = UI.Vertical()
			.WithStyles(Card)
			.Add(Header(), Divider(), StatusBar(viewer), Grid(viewer));

		return Finished ? card.Add(NewRoundBar()) : card;
	}

	private UINode Header() => UI.Horizontal()
		.WithStyle("width", "100%")
		.WithStyle("padding", "10px 14px 8px 14px")
		.WithStyle("background-color", $"{Accent}10")
		.Add(
			UI.Label("title", "TIC TAC TOE")
				.WithStyle("font-size", "15px")
				.WithStyle("letter-spacing", "2px")
				.WithStyle("color", "#FFEFD7"),
			Spacer(),
			UI.Label("score", $"{_scoreX} - {_scoreO}")
				.WithStyle("font-size", "14px")
				.WithStyle("color", Accent)
				.WithStyle("margin-top", "1px"));

	private UINode StatusBar(char viewer) => UI.Horizontal()
		.WithStyle("width", "100%")
		.WithStyle("padding", "8px 14px 2px 14px")
		.Add(
			UI.Label("seat", viewer == None ? "Spectating" : $"You are {viewer}")
				.WithStyle("font-size", "12px")
				.WithStyle("color", "#FFEFD766"),
			Spacer(),
			UI.Label("status", StatusFor(viewer))
				.WithStyle("font-size", "12px")
				.WithStyle("color", "#FFEFD7"));

	private UINode Grid(char viewer) => UI.Vertical()
		.WithStyle("horizontal-align", "center")
		.WithStyle("margin", "8px 0px 8px 0px")
		.Add(Row(0, viewer), Row(3, viewer), Row(6, viewer));

	private UINode Row(int first, char viewer) =>
		UI.Horizontal().Add(Cell(first, viewer), Cell(first + 1, viewer), Cell(first + 2, viewer));

	private UINode Cell(int index, char viewer) {
		char mark = _cells[index];
		var cell = UI.Button("b" + index, mark == None ? "" : mark.ToString())
			.WithStyles(CellSize)
			.WithStyles(SkinFor(index, viewer))
			.WithTransition("background-color", "0.1s")
			.OnClick("move", index.ToString())
			.WithTextStyles(MarkText)
			.WithTextStyle("color", mark == 'X' ? XColour : OColour);

		return CanPlay(viewer, index)
			? cell.WithHoverStyles(HoveredCell).WithPressStyles(PressedCell)
			: cell;
	}

	private (string, string)[] SkinFor(int cell, char viewer) {
		if (IsWinningCell(cell)) return WinningCell;
		if (_cells[cell] != None) return TakenCell;
		return CanPlay(viewer, cell) ? PlayableCell : IdleCell;
	}

	private static UINode NewRoundBar() => UI.Horizontal()
		.WithStyle("width", "100%")
		.WithStyle("padding", "0px 14px 10px 14px")
		.Add(
			Spacer(),
			UI.Button("restartButton", "NEW ROUND")
				.OnClick("restart")
				.WithStyle("padding", "5px 12px")
				.WithStyle("border-radius", "2px")
				.WithStyle("background-color", $"{Accent}1A")
				.WithStyle("border", $"1px solid {Accent}44")
				.WithTransition("background-color", "0.1s")
				.WithHoverStyle("background-color", $"{Accent}3A")
				.WithPressStyle("background-color", $"{Accent}66")
				.WithTextStyle("font-size", "10px")
				.WithTextStyle("letter-spacing", "1px")
				.WithTextStyle("color", "#FFD5CE"));

	private static UINode Spacer() =>
		UI.Container().WithStyle("width", "fill-parent-flow(1.0)");

	private static UINode Divider() => UI.Container()
		.WithStyle("width", "100%")
		.WithStyle("height", "1px")
		.WithStyle("background-color", $"{Accent}33");
}
