using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;
using Tideline.Data.Services;

namespace Tideline.Cli;

/// <summary>
/// People are added here, on the machine the app runs on: there is no public sign-up page (docs/09-security.md). Each
/// new person gets a one-time setup link, good for 24 hours, where they choose a password and set up their authenticator.
/// </summary>
public static class UserCommands
{
    public const string Usage = """
        tideline user <command>

          add --email <e> --name <n> [--role owner|member|viewer]   add a person and print their setup link (default role: member)
          list                                                      everyone, with role and whether the second step is set up
          link --email <e>                                          a new setup link (also the way to reset a forgotten password)
          reset-mfa --email <e>                                     remove their authenticator and sign them out; they set it up again from a new link
          role --email <e> --role owner|member|viewer               change a role
          deactivate --email <e>                                    stop them signing in (activate puts them back)
        """;

    public static int Run(IReadOnlyList<string> args, Dictionary<string, string?> options, string connectionString, TimeProvider clock, TextWriter output)
    {
        var factory = new SqliteConnectionFactory(connectionString);
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var users = new UserRepository(conn, clock, tx);
        var audit = new AuditWriter(conn, clock, tx, Actor.System);

        UserRecord Person()
        {
            var email = options.GetValueOrDefault("email") ?? throw new InvalidDataException("Say who: --email someone@example.com");
            return users.ByEmail(email) ?? throw new InvalidDataException($"There is no one with the email {email}.");
        }

        switch (args.FirstOrDefault())
        {
            case "add":
            {
                var email = options.GetValueOrDefault("email") ?? throw new InvalidDataException("Say who: --email someone@example.com");
                var name = options.GetValueOrDefault("name") ?? throw new InvalidDataException("Give a name: --name \"Their Name\"");
                var role = ParseRole(options.GetValueOrDefault("role") ?? "member");
                if (!email.Contains('@') || email.Length > 254) throw new InvalidDataException($"'{email}' does not look like an email address.");
                if (users.ByEmail(email) is not null) throw new InvalidDataException($"{email} is already a user. Use `user link` for a new setup link.");

                var id = users.Create(email, name, role);
                audit.Write("user.created", "user", id.ToString(), null, new { email = UserRepository.NormalizeEmail(email), displayName = name, role = role.ToDb() });
                var link = NewLink(new SetupLinks(conn, clock, tx), audit, id, options);
                tx.Commit();
                Print(output, $"Added {name} ({UserRepository.NormalizeEmail(email)}) as {role.ToDb()}.", link);
                return 0;
            }
            case "list":
            {
                output.WriteLine($"{"Email",-34} {"Name",-22} {"Role",-8} {"Second step",-12} Status");
                foreach (var u in users.All())
                    output.WriteLine($"{u.Email,-34} {u.DisplayName,-22} {u.Role.ToDb(),-8} {(u.MfaEnrolledAt is null ? "not set up" : "set up"),-12} {(!u.IsActive ? "inactive" : u.HasPassword ? "active" : "waiting for setup")}");
                return 0;
            }
            case "link":
            {
                var u = Person();
                if (!u.IsActive) throw new InvalidDataException($"{u.Email} is inactive. Run `user activate` first.");
                var link = NewLink(new SetupLinks(conn, clock, tx), audit, u.Id, options);
                tx.Commit();
                Print(output, $"New setup link for {u.DisplayName} ({u.Email}). Any older link no longer works.", link);
                return 0;
            }
            case "reset-mfa":
            {
                var u = Person();
                new MfaRepository(conn, clock, tx).DeleteAll(u.Id);
                users.SetMfaEnrolled(u.Id, null);
                var signedOut = new SessionRepository(conn, clock, tx).RevokeAllForUser(u.Id);
                audit.Write("mfa.reset", "user", u.Id.ToString(), new { enrolled = u.MfaEnrolledAt is not null }, new { enrolled = false, sessionsEnded = signedOut });
                var link = NewLink(new SetupLinks(conn, clock, tx), audit, u.Id, options);
                tx.Commit();
                Print(output, $"Removed the authenticator for {u.DisplayName} and signed them out everywhere.", link);
                return 0;
            }
            case "role":
            {
                var u = Person();
                var role = ParseRole(options.GetValueOrDefault("role") ?? throw new InvalidDataException("Say which role: --role owner|member|viewer"));
                if (u.Role == Role.Owner && role != Role.Owner && users.ActiveOwners() <= 1)
                    throw new InvalidDataException("That would leave no owner. Make someone else an owner first.");
                users.SetRole(u.Id, role);
                audit.Write("user.role_changed", "user", u.Id.ToString(), new { role = u.Role.ToDb() }, new { role = role.ToDb() });
                tx.Commit();
                output.WriteLine($"{u.DisplayName} is now {role.ToDb()}.");
                return 0;
            }
            case "deactivate" or "activate":
            {
                var u = Person();
                var active = args[0] == "activate";
                if (!active && u.Role == Role.Owner && users.ActiveOwners() <= 1) throw new InvalidDataException("That would leave no owner.");
                users.SetActive(u.Id, active);
                if (!active) new SessionRepository(conn, clock, tx).RevokeAllForUser(u.Id);
                audit.Write(active ? "user.activated" : "user.deactivated", "user", u.Id.ToString(), new { isActive = u.IsActive }, new { isActive = active });
                tx.Commit();
                output.WriteLine($"{u.DisplayName} is now {(active ? "active" : "inactive and signed out everywhere")}.");
                return 0;
            }
            default:
                output.WriteLine(Usage);
                return args.Count == 0 ? 1 : 1;
        }
    }

    private static Role ParseRole(string text) =>
        Enum.TryParse<Role>(text.Trim(), ignoreCase: true, out var r) ? r : throw new InvalidDataException($"'{text}' is not a role. Use owner, member or viewer.");

    private static (string Url, DateTimeOffset Expires) NewLink(SetupLinks links, AuditWriter audit, long userId, Dictionary<string, string?> options)
    {
        var (token, expires) = links.Create(userId);
        var baseUrl = (options.GetValueOrDefault("url") ?? Environment.GetEnvironmentVariable("Tideline__PublicUrl") ?? "http://localhost:5173").TrimEnd('/');
        audit.Write("user.setup_link_created", "user", userId.ToString(), null, new { expires });
        return ($"{baseUrl}/setup?token={token}", expires);
    }

    private static void Print(TextWriter output, string what, (string Url, DateTimeOffset Expires) link)
    {
        output.WriteLine(what);
        output.WriteLine();
        output.WriteLine("Setup link (works once, until " + link.Expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "):");
        output.WriteLine("  " + link.Url);
        output.WriteLine();
        output.WriteLine("They open it, choose a password, and scan the QR code with an authenticator app.");
    }
}
