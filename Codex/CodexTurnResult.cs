namespace Symphony.Codex;

public enum CodexTurnOutcome {
	Completed,
	Failed,
	Cancelled,
	InputRequired
}

public sealed record CodexTurnResult(
	string ThreadId,
	string TurnId,
	CodexTurnOutcome Outcome,
	string? ErrorMessage);
