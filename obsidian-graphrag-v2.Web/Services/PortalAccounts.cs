using System.Security.Claims;
using System.Security.Cryptography;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace obsidian_graphrag_v2.Web.Services;

internal sealed class PortalAccounts
{
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
    }

    private string SetupPath => Path.Combine(_accountsPath, "owner-setup");
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

    public string WorkspacePath(string id) => Path.Combine(Root, ".workspaces", id);
    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public bool NeedsOwner { get { lock (_gate) return !Directory.EnumerateFiles(_accountsPath, "*.json").Any(); } }

    public bool Register(string username, string password, string token, bool owner)
    {
        var minimumPasswordLength = owner ? 4 : 12;
        if (!IsEmail(username) || password.Length < minimumPasswordLength || password.Length > 128 ||
            token.Length != 64 || !token.All(Uri.IsHexDigit))
            return false;

        lock (_gate)
        {
            var invitation = owner ? SetupPath : Path.Combine(_accountsPath, "invite-" + token);
            if (owner != NeedsOwner || File.Exists(AccountPath(username)) || !File.Exists(invitation) ||
                (owner && !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(invitation)), System.Text.Encoding.UTF8.GetBytes(token))) ||
                (!owner && DateTime.UtcNow - File.GetCreationTimeUtc(invitation) > TimeSpan.FromHours(24)))
                return false;

            // Use a stable owner directory so an interrupted initial copy can be retried.
            var account = new Account(owner ? "owner" : Guid.NewGuid().ToString("N"), username, "", owner);
            var workspace = WorkspacePath(account.Id);
            Directory.CreateDirectory(workspace);
            if (owner)
                CopyLegacyKnowledge(workspace);
            account = account with { PasswordHash = _hasher.HashPassword(account, password) };
            var path = AccountPath(username);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(account));
            File.Move(path + ".tmp", path);
            File.Delete(invitation);
            return true;
        }
    }

    public ClaimsPrincipal? SignIn(string username, string password)
    {
        if (!(IsEmail(username) || IsLegacyUsername(username)) || password.Length is < 4 or > 128)
            return null;

        lock (_gate)
        {
            var path = AccountPath(username);
            if (!File.Exists(path))
                return null;
            var account = JsonSerializer.Deserialize<Account>(File.ReadAllText(path));
            if (account is null || _hasher.VerifyHashedPassword(account, account.PasswordHash, password) == PasswordVerificationResult.Failed)
                return null;

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, account.Id),
                new(ClaimTypes.Name, account.Username),
                new("session", NewToken())
            };
            if (account.Owner)
                claims.Add(new(ClaimTypes.Role, "Owner"));
            var sessionId = claims[2].Value;
            foreach (var expired in _sessions.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
                _sessions.Remove(expired);
            _sessions.Add(sessionId, new Session(account.Id, DateTimeOffset.UtcNow.AddHours(12)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Portal"));
        }
    }

    public string? GetWorkspaceId(ClaimsPrincipal user)
    {
        lock (_gate)
        {
            var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var key = user.FindFirstValue("session");
            return key is not null && _sessions.TryGetValue(key, out var session) &&
                session.Id == id && session.Expires > DateTimeOffset.UtcNow ? id : null;
        }
    }

    public void SignOut(ClaimsPrincipal user)
    {
        lock (_gate)
            if (user.FindFirstValue("session") is { } key)
                _sessions.Remove(key);
    }

    public string CreateInvitation()
    {
        lock (_gate)
        {
            foreach (var path in Directory.EnumerateFiles(_accountsPath, "invite-*"))
                if (DateTime.UtcNow - File.GetCreationTimeUtc(path) > TimeSpan.FromHours(24))
                    File.Delete(path);
            var token = NewToken();
            File.WriteAllText(Path.Combine(_accountsPath, "invite-" + token), "");
            return token;
        }
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

    private sealed record Account(string Id, string Username, string PasswordHash, bool Owner);
    private sealed record Session(string Id, DateTimeOffset Expires);
}
