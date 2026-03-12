namespace Symphony.Configuration;

public sealed class WorkflowLoadException : Exception {
	public WorkflowLoadException(string code, string message, Exception? innerException = null)
		: base(message, innerException) {
		Code = code;
	}

	public string Code { get; }
}
