namespace Symphony.Workspace;

public sealed class WorkspaceHookException : Exception {
	public WorkspaceHookException(string hookName, string message, Exception? innerException = null)
		: base(message, innerException) {
		HookName = hookName;
	}

	public string HookName { get; }
}
