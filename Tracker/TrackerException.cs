namespace Symphony.Tracker;

public sealed class TrackerException : Exception {
	public TrackerException(string code, string message, Exception? innerException = null)
		: base(message, innerException) {
		Code = code;
	}

	public string Code { get; }
}
