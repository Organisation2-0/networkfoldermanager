namespace NetworkFolderManager.Server;

public sealed class SessionRecord
{
    public string TokenHash { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}

public sealed class SessionsDb
{
    public List<SessionRecord> Sessions { get; set; } = new();
}

/// <summary>
/// Anmeldungen (gespeichert, damit man nach einem Neustart angemeldet bleibt). Nur der Hash des Tokens wird gespeichert.
/// Eine Sitzung läuft nach 30 Tagen ohne Nutzung ab.
/// </summary>
public sealed class SessionStore
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);
    private readonly JsonFile<SessionsDb> _db;

    public SessionStore(string file)
    {
        _db = new JsonFile<SessionsDb>(file);
    }

    public string Create(string userId, string? device)
    {
        var token = Tokens.New();
        _db.Update(db => db.Sessions.Add(new SessionRecord
        {
            TokenHash = Tokens.Hash(token),
            UserId = userId,
            Device = (device ?? string.Empty).Trim() is { Length: > 0 } d ? d[..Math.Min(d.Length, 80)] : "Unbekannt"
        }));
        return token;
    }

    /// <summary>Benutzer-ID zum Token oder null.</summary>
    public string? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var hash = Tokens.Hash(token);
        var now = DateTime.UtcNow;
        var session = _db.Read(db => db.Sessions.FirstOrDefault(s => s.TokenHash == hash));
        if (session == null || session.LastUsed < now - IdleLifetime)
            return null;

        // Nutzung nur gelegentlich speichern (nicht bei jeder Anfrage auf die Platte schreiben)
        if (session.LastUsed < now - TimeSpan.FromHours(1))
            _db.Update(db =>
            {
                var s = db.Sessions.FirstOrDefault(x => x.TokenHash == hash);
                if (s != null)
                    s.LastUsed = now;
            });
        return session.UserId;
    }

    public void Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return;
        var hash = Tokens.Hash(token);
        _db.Update(db => db.Sessions.RemoveAll(s => s.TokenHash == hash));
    }

    /// <summary>Alle Sitzungen eines Benutzers beenden (außer optional der aktuellen).</summary>
    public void RevokeUser(string userId, string? exceptToken = null)
    {
        var keep = exceptToken == null ? null : Tokens.Hash(exceptToken);
        _db.Update(db => db.Sessions.RemoveAll(s => s.UserId == userId && s.TokenHash != keep));
    }

    public void Cleanup()
    {
        var limit = DateTime.UtcNow - IdleLifetime;
        if (_db.Read(db => db.Sessions.Any(s => s.LastUsed < limit)))
            _db.Update(db => db.Sessions.RemoveAll(s => s.LastUsed < limit));
    }
}
