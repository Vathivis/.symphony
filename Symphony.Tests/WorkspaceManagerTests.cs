using Symphony.Workspace;

namespace Symphony.Tests;

public sealed class WorkspaceManagerTests {
	[Fact]
	public void SanitizeWorkspaceKey_ReplacesUnsupportedCharacters() {
		var sanitized = WorkspaceManager.SanitizeWorkspaceKey("ABC-123 feature/branch?");

		Assert.Equal("ABC-123_feature_branch_", sanitized);
	}

	[Fact]
	public void EnsureWithinRoot_RejectsPathsOutsideWorkspaceRoot() {
		var workspaceRoot = Path.Combine(Path.GetTempPath(), "symphony-root-" + Guid.NewGuid().ToString("N"));
		var escapedPath = Path.GetFullPath(Path.Combine(workspaceRoot, "..", "outside"));

		Assert.Throws<InvalidOperationException>(() =>
			WorkspaceManager.EnsureWithinRoot(workspaceRoot, escapedPath));
	}
}
