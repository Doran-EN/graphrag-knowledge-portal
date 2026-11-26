using System.Security.Claims;
using System.Security.Cryptography;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace obsidian_graphrag_v2.Web.Services;

internal sealed class PortalAccounts
{
    internal const int MinimumPasswordLength = 4;
    internal const int MaximumPasswordLength = 128;
    internal const int MaximumDisplayNameLength = 64;
    private const string DisplayNameClaim = "portal:display-name";

    private readonly object _gate = new();
    private readonly string _accountsPath;
    private readonly PasswordHasher<Account> _hasher = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public string Root { get; }
    public string KeysPath => Path.Combine(_accountsPath, "keys");

    public PortalAccounts(IConfiguration configuration)
    {
        Root = Path.GetFullPath(configuration["KnowledgeStorage:RootPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "knowledge"));
        _accountsPath = Path.Combine(Root, ".accounts");
        Directory.CreateDirectory(_accountsPath);
        if (!Directory.EnumerateFiles(_accountsPath, "*.json").Any() && !File.Exists(SetupPath))
            File.WriteAllText(SetupPath, NewToken());

        ApplyExistingDisplayNames(configuration);
    }

    private string SetupPath => Path.Combine(_accountsPath, "owner-setup");
    private string DepartmentsPath => Path.Combine(_accountsPath, "departments", "catalog.json");

    private string AccountPath(string login)
    {
        var normalized = login.ToLowerInvariant();
        // Keep existing username accounts readable. Email addresses never become filesystem paths.
        if (IsLegacyUsername(normalized))
            return Path.Combine(_accountsPath, normalized + ".json");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_accountsPath, "email-" + key + ".json");
    }

    private static bool IsLegacyUsername(string login) =>
        System.Text.RegularExpressions.Regex.IsMatch(login, @"\A[A-Za-z0-9][A-Za-z0-9_-]{2,31}\z");

    private static bool IsEmail(string login) =>
        login.Length <= 254 && MailAddress.TryCreate(login, out var address) &&
        address.Address == login && address.Host.Contains('.');

    private const string DepartmentPrefix = "department:";

    public string WorkspacePath(string id) => id.StartsWith(DepartmentPrefix, StringComparison.Ordinal)
        ? Path.Combine(Root, ".departments", id[DepartmentPrefix.Length..])
        : Path.Combine(Root, ".workspaces", id);
    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public bool NeedsOwner { get { lock (_gate) return !Directory.EnumerateFiles(_accountsPath, "*.json").Any(); } }

    internal static bool IsDisplayNameValid(string name) =>
        name.Length is > 0 and <= MaximumDisplayNameLength && !name.Any(char.IsControl);

    internal static string DisplayNameFor(ClaimsPrincipal user) =>
        user.FindFirstValue(DisplayNameClaim) ?? user.Identity?.Name ?? "Your account";

    public bool Register(string login, string password, string displayName, bool owner, string setupCode = "")
    {
        displayName = displayName.Trim();
        if (!IsDisplayNameValid(displayName) || !IsEmail(login) ||
            password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
            return false;

        lock (_gate)
        {
            if (owner != NeedsOwner || File.Exists(AccountPath(login)))
                return false;

            if (owner)
            {
                if (setupCode.Length != 64 || !File.Exists(SetupPath) || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(File.ReadAllText(SetupPath)), Encoding.UTF8.GetBytes(setupCode)))
                    return false;

                // Use a stable owner directory so an interrupted initial copy can be retried.
                var workspace = WorkspacePath("owner");
                Directory.CreateDirectory(workspace);
                CopyLegacyKnowledge(workspace);
            }
            var account = new Account(owner ? "owner" : Guid.NewGuid().ToString("N"), login, "", owner,
                owner ? AccountStatus.Active : AccountStatus.Pending, owner ? null : DateTimeOffset.UtcNow,
                DisplayName: displayName);
            account = account with { PasswordHash = _hasher.HashPassword(account, password) };
            SaveAccount(account);
            if (owner)
                File.Delete(SetupPath);
            return true;
        }
    }

    public ClaimsPrincipal? SignIn(string username, string password, out AccountStatus? blockedStatus)
    {
        blockedStatus = null;
        if (!(IsEmail(username) || IsLegacyUsername(username)) || password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
            return null;

        lock (_gate)
        {
            var path = AccountPath(username);
            if (!File.Exists(path))
                return null;
            var account = JsonSerializer.Deserialize<Account>(File.ReadAllText(path));
            if (account is null || _hasher.VerifyHashedPassword(account, account.PasswordHash, password) == PasswordVerificationResult.Failed)
                return null;
            if (account.Status != AccountStatus.Active)
            {
                blockedStatus = account.Status;
                return null;
            }

            var sessionId = NewToken();
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, account.Id),
                new(ClaimTypes.Name, account.Username),
                new(DisplayNameClaim, account.DisplayName ?? account.Username),
                new("session", sessionId)
            };
            if (account.Owner)
                claims.Add(new(ClaimTypes.Role, "Owner"));
            foreach (var expired in _sessions.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
                _sessions.Remove(expired);
            _sessions.Add(sessionId, new Session(account.Id, account.DepartmentId, DateTimeOffset.UtcNow.AddHours(12)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Portal"));
        }
    }

    public string? GetWorkspaceId(ClaimsPrincipal user, string? departmentId = null)
    {
        lock (_gate)
        {
            if (FindSession(user) is not { } session)
                return null;
            if (string.IsNullOrEmpty(departmentId))
                return session.Id;

            return (user.IsInRole("Owner") || session.DepartmentId == departmentId) &&
                DepartmentExists(departmentId) ? DepartmentPrefix + departmentId : null;
        }
    }

    public IReadOnlyList<Department> GetWorkspaceDepartments(ClaimsPrincipal user)
    {
        lock (_gate)
        {
            if (FindSession(user) is not { } session)
                return [];

            return ReadDepartments()
                .Where(department => user.IsInRole("Owner") || department.Id == session.DepartmentId)
                .OrderBy(department => department.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private Session? FindSession(ClaimsPrincipal user)
    {
        var key = user.FindFirstValue("session");
        return key is not null && _sessions.TryGetValue(key, out var session) &&
            session.Id == user.FindFirstValue(ClaimTypes.NameIdentifier) && session.Expires > DateTimeOffset.UtcNow
            ? session : null;
    }

    public void SignOut(ClaimsPrincipal user)
    {
        lock (_gate)
            if (user.FindFirstValue("session") is { } key)
                _sessions.Remove(key);
    }

    public IReadOnlyList<UserAccount> GetUsers(ClaimsPrincipal owner)
    {
        lock (_gate)
        {
            RequireOwner(owner);
            return ReadAccounts()
                .OrderBy(account => account.Status != AccountStatus.Pending)
                .ThenByDescending(account => account.Owner)
                .ThenBy(account => account.RequestedAt)
                .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
                .Select(account => new UserAccount(account.Id, account.Username, account.DisplayName ?? account.Username, account.Owner, account.Status,
                    account.RequestedAt, account.DepartmentId))
                .ToArray();
        }
    }

    public IReadOnlyList<Department> GetDepartments(ClaimsPrincipal owner)
    {
        lock (_gate)
        {
            RequireOwner(owner);
            return ReadDepartments().OrderBy(department => department.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public string? CreateDepartment(ClaimsPrincipal owner, string name, string description)
    {
        lock (_gate)
        {
            RequireOwner(owner);
            name = name.Trim();
            description = description.Trim();
            if (name.Length is 0 or > 80)
                return "Enter a department name of up to 80 characters.";
            if (description.Length > 240)
                return "Enter a description of up to 240 characters.";

            var departments = ReadDepartments();
            if (departments.Any(department => StringComparer.OrdinalIgnoreCase.Equals(department.Name, name)))
                return "A department with that name already exists.";

            departments.Add(new Department(Guid.NewGuid().ToString("N"), name, description));
            var path = DepartmentsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SaveJson(path, departments, overwrite: true);
            return null;
        }
    }

    public bool ReviewRequest(ClaimsPrincipal owner, string accountId, bool approve, string? departmentId)
    {
        lock (_gate)
        {
            RequireOwner(owner);
            var account = ReadAccounts().FirstOrDefault(account => account.Id == accountId);
            if (account is not { Owner: false, Status: AccountStatus.Pending })
                return false;

            if (approve)
            {
                if (!DepartmentExists(departmentId))
                    return false;
                Directory.CreateDirectory(WorkspacePath(account.Id));
            }

            SaveAccount(account with
            {
                Status = approve ? AccountStatus.Active : AccountStatus.Rejected,
                DepartmentId = approve ? departmentId : null
            }, overwrite: true);
            return true;
        }
    }

    public bool AssignDepartment(ClaimsPrincipal owner, string accountId, string departmentId)
    {
        lock (_gate)
        {
            RequireOwner(owner);
            var account = ReadAccounts().FirstOrDefault(account => account.Id == accountId);
            if (account is not { Owner: false, Status: AccountStatus.Active } || !DepartmentExists(departmentId))
                return false;
            if (account.DepartmentId == departmentId)
                return true;

            SaveAccount(account with { DepartmentId = departmentId }, overwrite: true);
            foreach (var session in _sessions.Where(entry => entry.Value.Id == account.Id).Select(entry => entry.Key).ToArray())
                _sessions.Remove(session);
            return true;
        }
    }

    private bool DepartmentExists(string? departmentId) =>
        !string.IsNullOrEmpty(departmentId) && ReadDepartments().Any(department => department.Id == departmentId);

    private List<Department> ReadDepartments()
    {
        var path = DepartmentsPath;
        return File.Exists(path) ? JsonSerializer.Deserialize<List<Department>>(File.ReadAllText(path)) ?? [] : [];
    }

    private void RequireOwner(ClaimsPrincipal user)
    {
        if (!user.IsInRole("Owner") || GetWorkspaceId(user) is null)
            throw new UnauthorizedAccessException("Only the portal owner can manage user accounts.");
    }

    private IEnumerable<Account> ReadAccounts() => Directory.EnumerateFiles(_accountsPath, "*.json")
        .Select(path => JsonSerializer.Deserialize<Account>(File.ReadAllText(path))).OfType<Account>();

    private void ApplyExistingDisplayNames(IConfiguration configuration)
    {
        var ownerName = configuration["PortalAccounts:ExistingOwnerName"]?.Trim();
        var memberName = configuration["PortalAccounts:ExistingSingleMemberName"]?.Trim();
        if (ownerName is null || memberName is null || !IsDisplayNameValid(ownerName) || !IsDisplayNameValid(memberName))
            return;

        var activeAccounts = ReadAccounts().Where(account => account.Status == AccountStatus.Active).Take(3).ToArray();
        if (activeAccounts.Length != 2 || activeAccounts[0].Owner == activeAccounts[1].Owner)
            return;

        foreach (var account in activeAccounts.Where(account => account.DisplayName is null))
            SaveAccount(account with { DisplayName = account.Owner ? ownerName : memberName }, overwrite: true);
    }

    private void SaveAccount(Account account, bool overwrite = false) =>
        SaveJson(AccountPath(account.Username), account, overwrite);

    private static void SaveJson<T>(string path, T value, bool overwrite)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, overwrite);
    }

    private void CopyLegacyKnowledge(string workspace)
    {
        foreach (var name in new[] { "raw", "wiki", ".semantic", ".backups" })
        {
            var source = Path.Combine(Root, name);
            if (!Directory.Exists(source))
                continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(workspace, name, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }
        var schema = Path.Combine(Root, "TheSchema.md");
        if (File.Exists(schema))
            File.Copy(schema, Path.Combine(workspace, "TheSchema.md"), overwrite: true);
    }

    // Existing account files have no status field and remain active.
    private sealed record Account(string Id, string Username, string PasswordHash, bool Owner,
        AccountStatus Status = AccountStatus.Active, DateTimeOffset? RequestedAt = null, string? DepartmentId = null,
        string? DisplayName = null);
    public enum AccountStatus { Active, Pending, Rejected }
    public sealed record UserAccount(string Id, string Login, string DisplayName, bool Owner, AccountStatus Status, DateTimeOffset? RequestedAt,
        string? DepartmentId);
    public sealed record Department(string Id, string Name, string Description);
    private sealed record Session(string Id, string? DepartmentId, DateTimeOffset Expires);
}
