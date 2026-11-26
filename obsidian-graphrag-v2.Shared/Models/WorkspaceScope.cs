namespace ObsidianGraphRag.Shared.Models;

public sealed record WorkspaceScope(string Name, string? DepartmentId, Func<bool> CanAccess)
{
    public static string Link(string path, string? departmentId) => string.IsNullOrEmpty(departmentId)
        ? path
        : path + (path.Contains('?') ? "&" : "?") + "department=" + Uri.EscapeDataString(departmentId);
}
