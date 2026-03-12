namespace Symphony.Codex;

public sealed class CodexClientException : Exception {
	public CodexClientException(string code, string message, Exception? innerException = null)
		: base(message, innerException) {
		Code = code;
	}

	public string Code { get; }
}
