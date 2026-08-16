using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using MySqlConnector;

// Scheme name for the short-lived cookie that holds Google's identity for just the few seconds
// of the OAuth round trip - it's never used for real authorization. /api/auth/google/finish reads
// it once, looks the person up in our own `users` table, and issues our normal "Cookies" session
// (same claims shape as username/password login) instead - so every existing RequireAuthorization
// policy keeps working unchanged regardless of how someone signed in.
const string GoogleExternalScheme = "GoogleExternal";

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DeskFlow")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DeskFlow in appsettings.json");

builder.Services.AddSingleton(new MySqlConnectionFactory(connectionString));

// Google sign-in is entirely optional - it only turns on once an admin pastes a real Client
// ID/Secret into appsettings.json. Left blank (the default), none of the Google auth handlers
// are registered and none of the /api/auth/google/* endpoints exist, so there's no dead button
// or startup risk from an unconfigured feature.
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
var googleSignInEnabled = !string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret);

var authBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "deskflow_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        // Return a plain 401/403 with a short JSON message instead of redirecting to a
        // (non-existent) login page - the frontend reads the status code itself, and anyone
        // who hits a protected URL directly (browser address bar, curl, etc.) sees a clear
        // "you're not authorized" instead of a blank response or, worse, real data.
        options.Events.OnRedirectToLogin = async context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "You need to sign in to view this." });
        };
        options.Events.OnRedirectToAccessDenied = async context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "You're not authorized to view this." });
        };
    });

if (googleSignInEnabled)
{
    authBuilder
        // A short-lived cookie just for holding Google's raw identity during the OAuth round
        // trip - see the GoogleExternalScheme comment up top for why this exists.
        .AddCookie(GoogleExternalScheme, options =>
        {
            options.Cookie.Name = "deskflow_google_external";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        })
        .AddGoogle(options =>
        {
            options.ClientId = googleClientId!;
            options.ClientSecret = googleClientSecret!;
            options.SignInScheme = GoogleExternalScheme;
            if (!options.Scope.Contains("email")) options.Scope.Add("email");
            options.ClaimActions.MapJsonKey("email_verified", "email_verified");
        });
}

builder.Services.AddAuthorization(options =>
{
    // Account management (creating/editing/removing logins) sits behind this policy.
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
    // Day-to-day IT work - browsing the directory, managing assets - is open to agents too.
    options.AddPolicy("AgentOrAdmin", policy => policy.RequireRole("admin", "agent"));
});

var validRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "admin", "agent", "requester" };
var validAssetStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "available", "in_use", "under_repair", "retired" };
var validTicketPriorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high", "critical" };
var validTicketStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "new", "assigned", "in_progress", "on_hold", "resolved", "closed" };

// Payload obfuscation for /api/auth/login - see README for exactly what this does and doesn't protect
// against. This key has to also live in login.html's JavaScript so the browser can decrypt the
// response, so it is NOT a secret from anyone who reads the page source - only from someone glancing
// at the Network tab's response body without also digging into the JS.
var payloadKey = Convert.FromHexString("cee96d99d0bba84222cf96cea9b0b9c4eeb70af4c92a55a4586366d6db58c784");

var app = builder.Build();

// Serve wwwroot/login.html (and any future static assets) directly - no separate frontend server needed.
// UseDefaultFiles only looks for index.html/default.html by default, so tell it to treat
// login.html as the default document for "/" too.
var defaultFilesOptions = new DefaultFilesOptions();
defaultFilesOptions.DefaultFileNames.Insert(0, "login.html");
app.UseDefaultFiles(defaultFilesOptions);
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Create a default admin account the first time the app runs against an empty users table.
await SeedAdminAsync(app.Services.GetRequiredService<MySqlConnectionFactory>(), app.Logger);

app.MapPost("/api/auth/login", async (LoginRequest request, MySqlConnectionFactory db, HttpContext http) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return EncryptedResult(new { error = "Username and password are required." }, payloadKey, 400);

    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT id, username, email, full_name, role, password_hash, is_active
        FROM users
        WHERE username = @username OR email = @username
        LIMIT 1;";
    cmd.Parameters.AddWithValue("@username", request.Username);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return EncryptedResult(new { error = "Invalid username or password." }, payloadKey, 401);

    var isActive = Convert.ToBoolean(reader["is_active"]);
    var passwordHash = (string)reader["password_hash"];

    if (!isActive)
        return EncryptedResult(new { error = "This account has been disabled." }, payloadKey, 403);

    if (!BCrypt.Net.BCrypt.Verify(request.Password, passwordHash))
        return EncryptedResult(new { error = "Invalid username or password." }, payloadKey, 401);

    var id = Convert.ToInt32(reader["id"]);
    var username = (string)reader["username"];
    var email = (string)reader["email"];
    var fullName = (string)reader["full_name"];
    var role = (string)reader["role"];

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, id.ToString()),
        new(ClaimTypes.Name, username),
        new(ClaimTypes.Email, email),
        new(ClaimTypes.Role, role),
        new("full_name", fullName),
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    return EncryptedResult(new { id, username, email, fullName, role }, payloadKey, 200);
});

app.MapPost("/api/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out." });
}).RequireAuthorization();

app.MapGet("/api/auth/me", (HttpContext http) =>
{
    var user = http.User;
    return Results.Ok(new
    {
        id = user.FindFirstValue(ClaimTypes.NameIdentifier),
        username = user.Identity!.Name,
        email = user.FindFirstValue(ClaimTypes.Email),
        fullName = user.FindFirstValue("full_name"),
        role = user.FindFirstValue(ClaimTypes.Role),
    });
}).RequireAuthorization();

// login.html asks this on load to decide whether to even show the "Sign in with Google" button -
// keeps the button from appearing (and 404ing) on installs that haven't configured Google yet.
app.MapGet("/api/auth/providers", () => Results.Ok(new { google = googleSignInEnabled }));

if (googleSignInEnabled)
{
    // Kicks off the redirect to Google's consent screen. Google calls back to the default
    // /signin-google path (handled internally by the Google handler), which completes the
    // external sign-in against GoogleExternalScheme and then redirects here.
    app.MapGet("/api/auth/google/login", () =>
        Results.Challenge(
            new AuthenticationProperties { RedirectUri = "/api/auth/google/finish" },
            new[] { GoogleDefaults.AuthenticationScheme }));

    app.MapGet("/api/auth/google/finish", async (HttpContext http, MySqlConnectionFactory db) =>
    {
        var externalResult = await http.AuthenticateAsync(GoogleExternalScheme);
        // Whatever happens below, the temporary Google cookie has done its job - drop it now
        // so it never lingers as a second, unused session.
        await http.SignOutAsync(GoogleExternalScheme);

        if (!externalResult.Succeeded || externalResult.Principal is null)
            return Results.Redirect("/?error=google_failed");

        var externalUser = externalResult.Principal;
        var email = externalUser.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
            return Results.Redirect("/?error=google_no_email");

        // Defensive check: only trust the email if Google says it's verified. Google always
        // includes this claim for the "email" scope, but if it's ever absent, fail closed
        // rather than silently trusting an unverified address.
        var emailVerifiedClaim = externalUser.FindFirstValue("email_verified");
        if (!string.IsNullOrEmpty(emailVerifiedClaim) && !string.Equals(emailVerifiedClaim, "true", StringComparison.OrdinalIgnoreCase))
            return Results.Redirect("/?error=google_email_unverified");

        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, username, email, full_name, role, is_active
            FROM users
            WHERE email = @email
            LIMIT 1;";
        cmd.Parameters.AddWithValue("@email", email);

        await using var reader = await cmd.ExecuteReaderAsync();
        // By design (see README): Google sign-in only works for an email an admin already
        // created as a DeskFlow user. Nobody can self-signup their way into an account this way.
        if (!await reader.ReadAsync())
            return Results.Redirect("/?error=google_no_account");

        var isActive = Convert.ToBoolean(reader["is_active"]);
        if (!isActive)
            return Results.Redirect("/?error=google_disabled");

        var id = Convert.ToInt32(reader["id"]);
        var username = (string)reader["username"];
        var matchedEmail = (string)reader["email"];
        var fullName = (string)reader["full_name"];
        var role = (string)reader["role"];

        // Same claim shape as username/password login, so every existing RequireAuthorization
        // policy treats a Google-authenticated session identically to a normal one.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id.ToString()),
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Email, matchedEmail),
            new(ClaimTypes.Role, role),
            new("full_name", fullName),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return Results.Redirect("/app.html");
    });
}

// ---------------------------------------------------------------------------
// Users management (admin only) - the CRUD slice behind the "Users" nav item.
// ---------------------------------------------------------------------------
var users = app.MapGroup("/api/users");

users.MapGet("/", async (MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT id, username, email, full_name, role, is_active, created_at, updated_at
        FROM users
        ORDER BY created_at DESC;";

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        list.Add(MapUserRow(reader));

    return Results.Ok(list);
}).RequireAuthorization("AgentOrAdmin");

users.MapGet("/{id:int}", async (int id, MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT id, username, email, full_name, role, is_active, created_at, updated_at
        FROM users WHERE id = @id LIMIT 1;";
    cmd.Parameters.AddWithValue("@id", id);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.NotFound(new { error = "User not found." });

    return Results.Ok(MapUserRow(reader));
}).RequireAuthorization("AgentOrAdmin");

users.MapPost("/", async (CreateUserRequest request, MySqlConnectionFactory db) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Email) ||
        string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Username, email, full name, and password are required." });

    if (request.Password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters." });

    var role = request.Role?.Trim().ToLowerInvariant() ?? "";
    if (!validRoles.Contains(role))
        return Results.BadRequest(new { error = "Role must be admin, agent, or requester." });

    var hash = BCrypt.Net.BCrypt.HashPassword(request.Password);

    await using var conn = await db.OpenAsync();
    await using var insertCmd = conn.CreateCommand();
    insertCmd.CommandText = @"
        INSERT INTO users (username, email, password_hash, full_name, role, is_active)
        VALUES (@username, @email, @hash, @fullName, @role, @isActive);";
    insertCmd.Parameters.AddWithValue("@username", request.Username.Trim());
    insertCmd.Parameters.AddWithValue("@email", request.Email.Trim());
    insertCmd.Parameters.AddWithValue("@hash", hash);
    insertCmd.Parameters.AddWithValue("@fullName", request.FullName.Trim());
    insertCmd.Parameters.AddWithValue("@role", role);
    insertCmd.Parameters.AddWithValue("@isActive", request.IsActive);

    try
    {
        await insertCmd.ExecuteNonQueryAsync();
    }
    catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
    {
        return Results.Json(new { error = "That username or email is already in use." }, statusCode: 409);
    }

    await using var idCmd = conn.CreateCommand();
    idCmd.CommandText = "SELECT LAST_INSERT_ID();";
    var newId = Convert.ToInt32(await idCmd.ExecuteScalarAsync());

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = @"
        SELECT id, username, email, full_name, role, is_active, created_at, updated_at
        FROM users WHERE id = @id;";
    fetchCmd.Parameters.AddWithValue("@id", newId);
    await using var reader = await fetchCmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    return Results.Created($"/api/users/{newId}", MapUserRow(reader));
}).RequireAuthorization("AdminOnly");

users.MapPut("/{id:int}", async (int id, UpdateUserRequest request, MySqlConnectionFactory db) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.FullName))
        return Results.BadRequest(new { error = "Email and full name are required." });

    var role = request.Role?.Trim().ToLowerInvariant() ?? "";
    if (!validRoles.Contains(role))
        return Results.BadRequest(new { error = "Role must be admin, agent, or requester." });

    if (!string.IsNullOrEmpty(request.Password) && request.Password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters." });

    await using var conn = await db.OpenAsync();

    // Confirm the user exists and grab their current role for the "last admin" guard below.
    string currentRole;
    await using (var checkCmd = conn.CreateCommand())
    {
        checkCmd.CommandText = "SELECT role FROM users WHERE id = @id LIMIT 1;";
        checkCmd.Parameters.AddWithValue("@id", id);
        var result = await checkCmd.ExecuteScalarAsync();
        if (result is null)
            return Results.NotFound(new { error = "User not found." });
        currentRole = (string)result;
    }

    if (currentRole == "admin" && role != "admin")
    {
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM users WHERE role = 'admin';";
        var adminCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        if (adminCount <= 1)
            return Results.BadRequest(new { error = "You can't remove the last admin's admin role." });
    }

    await using var updateCmd = conn.CreateCommand();
    if (!string.IsNullOrEmpty(request.Password))
    {
        updateCmd.CommandText = @"
            UPDATE users
            SET email = @email, full_name = @fullName, role = @role, is_active = @isActive, password_hash = @hash
            WHERE id = @id;";
        updateCmd.Parameters.AddWithValue("@hash", BCrypt.Net.BCrypt.HashPassword(request.Password));
    }
    else
    {
        updateCmd.CommandText = @"
            UPDATE users
            SET email = @email, full_name = @fullName, role = @role, is_active = @isActive
            WHERE id = @id;";
    }
    updateCmd.Parameters.AddWithValue("@email", request.Email.Trim());
    updateCmd.Parameters.AddWithValue("@fullName", request.FullName.Trim());
    updateCmd.Parameters.AddWithValue("@role", role);
    updateCmd.Parameters.AddWithValue("@isActive", request.IsActive);
    updateCmd.Parameters.AddWithValue("@id", id);

    try
    {
        await updateCmd.ExecuteNonQueryAsync();
    }
    catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
    {
        return Results.Json(new { error = "That email is already in use." }, statusCode: 409);
    }

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = @"
        SELECT id, username, email, full_name, role, is_active, created_at, updated_at
        FROM users WHERE id = @id;";
    fetchCmd.Parameters.AddWithValue("@id", id);
    await using var reader = await fetchCmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    return Results.Ok(MapUserRow(reader));
}).RequireAuthorization("AdminOnly");

users.MapDelete("/{id:int}", async (int id, MySqlConnectionFactory db, HttpContext http) =>
{
    var currentUserId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (currentUserId is not null && int.TryParse(currentUserId, out var selfId) && selfId == id)
        return Results.BadRequest(new { error = "You can't delete your own account while signed in." });

    await using var conn = await db.OpenAsync();

    string role;
    await using (var checkCmd = conn.CreateCommand())
    {
        checkCmd.CommandText = "SELECT role FROM users WHERE id = @id LIMIT 1;";
        checkCmd.Parameters.AddWithValue("@id", id);
        var result = await checkCmd.ExecuteScalarAsync();
        if (result is null)
            return Results.NotFound(new { error = "User not found." });
        role = (string)result;
    }

    if (role == "admin")
    {
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM users WHERE role = 'admin';";
        var adminCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        if (adminCount <= 1)
            return Results.BadRequest(new { error = "You can't delete the last remaining admin." });
    }

    await using var deleteCmd = conn.CreateCommand();
    deleteCmd.CommandText = "DELETE FROM users WHERE id = @id;";
    deleteCmd.Parameters.AddWithValue("@id", id);
    await deleteCmd.ExecuteNonQueryAsync();

    return Results.Ok(new { message = "User deleted." });
}).RequireAuthorization("AdminOnly");

// ---------------------------------------------------------------------------
// Asset management (agents + admins) - the CRUD slice behind the "Assets" nav item.
// ---------------------------------------------------------------------------
var assets = app.MapGroup("/api/assets").RequireAuthorization("AgentOrAdmin");

const string AssetSelectSql = @"
    SELECT a.id, a.asset_tag, a.name, a.type, a.serial_number, a.status,
           a.assigned_to_id, u.full_name AS assigned_to_name, u.username AS assigned_to_username,
           a.department, a.location,
           a.purchased_at, a.warranty_expires_at, a.notes, a.created_at, a.updated_at
    FROM assets a
    LEFT JOIN users u ON u.id = a.assigned_to_id";

assets.MapGet("/", async (MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = AssetSelectSql + " ORDER BY a.created_at DESC;";

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        list.Add(MapAssetRow(reader));

    return Results.Ok(list);
});

assets.MapGet("/{id:int}", async (int id, MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = AssetSelectSql + " WHERE a.id = @id LIMIT 1;";
    cmd.Parameters.AddWithValue("@id", id);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.NotFound(new { error = "Asset not found." });

    return Results.Ok(MapAssetRow(reader));
});

assets.MapPost("/", async (CreateAssetRequest request, MySqlConnectionFactory db) =>
{
    if (string.IsNullOrWhiteSpace(request.AssetTag) || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Type))
        return Results.BadRequest(new { error = "Asset tag, name, and type are required." });

    var status = string.IsNullOrWhiteSpace(request.Status) ? "available" : request.Status.Trim().ToLowerInvariant();
    if (!validAssetStatuses.Contains(status))
        return Results.BadRequest(new { error = "Status must be available, in_use, under_repair, or retired." });

    await using var conn = await db.OpenAsync();

    if (request.AssignedToId is int assignId)
    {
        await using var checkUserCmd = conn.CreateCommand();
        checkUserCmd.CommandText = "SELECT COUNT(*) FROM users WHERE id = @id;";
        checkUserCmd.Parameters.AddWithValue("@id", assignId);
        var exists = Convert.ToInt32(await checkUserCmd.ExecuteScalarAsync()) > 0;
        if (!exists)
            return Results.BadRequest(new { error = "The user you're assigning this to doesn't exist." });
    }

    await using var insertCmd = conn.CreateCommand();
    insertCmd.CommandText = @"
        INSERT INTO assets (asset_tag, name, type, serial_number, status, assigned_to_id, department, location, purchased_at, warranty_expires_at, notes)
        VALUES (@assetTag, @name, @type, @serialNumber, @status, @assignedToId, @department, @location, @purchasedAt, @warrantyExpiresAt, @notes);";
    insertCmd.Parameters.AddWithValue("@assetTag", request.AssetTag.Trim());
    insertCmd.Parameters.AddWithValue("@name", request.Name.Trim());
    insertCmd.Parameters.AddWithValue("@type", request.Type.Trim());
    insertCmd.Parameters.AddWithValue("@serialNumber", (object?)request.SerialNumber?.Trim() ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@status", status);
    insertCmd.Parameters.AddWithValue("@assignedToId", (object?)request.AssignedToId ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@department", (object?)request.Department?.Trim() ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@location", (object?)request.Location?.Trim() ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@purchasedAt", (object?)request.PurchasedAt ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@warrantyExpiresAt", (object?)request.WarrantyExpiresAt ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@notes", (object?)request.Notes?.Trim() ?? DBNull.Value);

    try
    {
        await insertCmd.ExecuteNonQueryAsync();
    }
    catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
    {
        return Results.Json(new { error = "That asset tag is already in use." }, statusCode: 409);
    }

    await using var idCmd = conn.CreateCommand();
    idCmd.CommandText = "SELECT LAST_INSERT_ID();";
    var newId = Convert.ToInt32(await idCmd.ExecuteScalarAsync());

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = AssetSelectSql + " WHERE a.id = @id;";
    fetchCmd.Parameters.AddWithValue("@id", newId);
    await using var reader = await fetchCmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    return Results.Created($"/api/assets/{newId}", MapAssetRow(reader));
});

assets.MapPut("/{id:int}", async (int id, UpdateAssetRequest request, MySqlConnectionFactory db) =>
{
    if (string.IsNullOrWhiteSpace(request.AssetTag) || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Type))
        return Results.BadRequest(new { error = "Asset tag, name, and type are required." });

    var status = request.Status?.Trim().ToLowerInvariant() ?? "";
    if (!validAssetStatuses.Contains(status))
        return Results.BadRequest(new { error = "Status must be available, in_use, under_repair, or retired." });

    await using var conn = await db.OpenAsync();

    await using (var checkCmd = conn.CreateCommand())
    {
        checkCmd.CommandText = "SELECT COUNT(*) FROM assets WHERE id = @id;";
        checkCmd.Parameters.AddWithValue("@id", id);
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;
        if (!exists)
            return Results.NotFound(new { error = "Asset not found." });
    }

    if (request.AssignedToId is int assignId)
    {
        await using var checkUserCmd = conn.CreateCommand();
        checkUserCmd.CommandText = "SELECT COUNT(*) FROM users WHERE id = @id;";
        checkUserCmd.Parameters.AddWithValue("@id", assignId);
        var exists = Convert.ToInt32(await checkUserCmd.ExecuteScalarAsync()) > 0;
        if (!exists)
            return Results.BadRequest(new { error = "The user you're assigning this to doesn't exist." });
    }

    await using var updateCmd = conn.CreateCommand();
    updateCmd.CommandText = @"
        UPDATE assets
        SET asset_tag = @assetTag, name = @name, type = @type, serial_number = @serialNumber,
            status = @status, assigned_to_id = @assignedToId, department = @department, location = @location,
            purchased_at = @purchasedAt, warranty_expires_at = @warrantyExpiresAt, notes = @notes
        WHERE id = @id;";
    updateCmd.Parameters.AddWithValue("@assetTag", request.AssetTag.Trim());
    updateCmd.Parameters.AddWithValue("@name", request.Name.Trim());
    updateCmd.Parameters.AddWithValue("@type", request.Type.Trim());
    updateCmd.Parameters.AddWithValue("@serialNumber", (object?)request.SerialNumber?.Trim() ?? DBNull.Value);
    updateCmd.Parameters.AddWithValue("@status", status);
    updateCmd.Parameters.AddWithValue("@assignedToId", (object?)request.AssignedToId ?? DBNull.Value);
    updateCmd.Parameters.AddWithValue("@department", (object?)request.Department?.Trim() ?? DBNull.Value);
    updateCmd.Parameters.AddWithValue("@location", (object?)request.Location?.Trim() ?? DBNull.Value);
    updateCmd.Parameters.AddWithValue("@purchasedAt", (object?)request.PurchasedAt ?? DBNull.Value);
    updateCmd.Parameters.AddWithValue("@warrantyExpiresAt", (object?)request.WarrantyExpiresAt ?? DBNull.Value);
    updateCmd.Parameters.AddWithValue("@notes", (object?)request.Notes?.Trim() ?? DBNull.Value);
    updateCmd.Parameters.AddWithValue("@id", id);

    try
    {
        await updateCmd.ExecuteNonQueryAsync();
    }
    catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
    {
        return Results.Json(new { error = "That asset tag is already in use." }, statusCode: 409);
    }

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = AssetSelectSql + " WHERE a.id = @id;";
    fetchCmd.Parameters.AddWithValue("@id", id);
    await using var reader = await fetchCmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    return Results.Ok(MapAssetRow(reader));
});

assets.MapDelete("/{id:int}", async (int id, MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();

    await using var checkCmd = conn.CreateCommand();
    checkCmd.CommandText = "SELECT COUNT(*) FROM assets WHERE id = @id;";
    checkCmd.Parameters.AddWithValue("@id", id);
    var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;
    if (!exists)
        return Results.NotFound(new { error = "Asset not found." });

    await using var deleteCmd = conn.CreateCommand();
    deleteCmd.CommandText = "DELETE FROM assets WHERE id = @id;";
    deleteCmd.Parameters.AddWithValue("@id", id);
    await deleteCmd.ExecuteNonQueryAsync();

    return Results.Ok(new { message = "Asset deleted." });
});

// ---------------------------------------------------------------------------
// Ticket management (everyone signed in) - the core help desk module. Requesters can file
// tickets and see/comment on their own; agents and admins can see and triage everything.
// There's deliberately no DELETE endpoint here - tickets are kept as records, not erased.
// ---------------------------------------------------------------------------
var tickets = app.MapGroup("/api/tickets").RequireAuthorization();

const string TicketSelectSql = @"
    SELECT t.id, t.ticket_number, t.subject, t.description, t.category, t.subcategory, t.priority, t.status,
           t.requester_id, ru.full_name AS requester_name, ru.username AS requester_username,
           t.assigned_technician_id, tu.full_name AS technician_name, tu.username AS technician_username,
           t.department, t.asset_id, a.asset_tag, a.name AS asset_name,
           t.resolution_notes, t.resolved_at, t.closed_at, t.created_at, t.updated_at,
           t.response_due_at, t.resolution_due_at, t.first_responded_at, t.response_met, t.resolution_met,
           t.on_hold_since, t.total_paused_minutes
    FROM tickets t
    LEFT JOIN users ru ON ru.id = t.requester_id
    LEFT JOIN users tu ON tu.id = t.assigned_technician_id
    LEFT JOIN assets a ON a.id = t.asset_id";

tickets.MapGet("/", async (HttpContext http, MySqlConnectionFactory db) =>
{
    var role = http.User.FindFirstValue(ClaimTypes.Role);
    var userId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    await using var conn = await db.OpenAsync();
    var policies = await GetSlaPoliciesAsync(conn);

    await using var cmd = conn.CreateCommand();
    if (role == "requester")
    {
        // Requesters only ever see their own tickets - enforced here, not just hidden in the UI.
        cmd.CommandText = TicketSelectSql + " WHERE t.requester_id = @uid ORDER BY t.created_at DESC;";
        cmd.Parameters.AddWithValue("@uid", userId);
    }
    else
    {
        cmd.CommandText = TicketSelectSql + " ORDER BY t.created_at DESC;";
    }

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        list.Add(MapTicketRow(reader, policies));

    return Results.Ok(list);
});

tickets.MapGet("/{id:int}", async (int id, HttpContext http, MySqlConnectionFactory db) =>
{
    var role = http.User.FindFirstValue(ClaimTypes.Role);
    var userId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    await using var conn = await db.OpenAsync();
    var policies = await GetSlaPoliciesAsync(conn);

    object ticket;
    int? requesterId;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = TicketSelectSql + " WHERE t.id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return Results.NotFound(new { error = "Ticket not found." });
        requesterId = reader["requester_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["requester_id"]);
        ticket = MapTicketRow(reader, policies);
    }

    if (role == "requester" && requesterId != userId)
        return Results.Json(new { error = "You're not authorized to view this ticket." }, statusCode: 403);

    var isAgentOrAdmin = role == "admin" || role == "agent";

    var comments = new List<object>();
    await using (var cCmd = conn.CreateCommand())
    {
        cCmd.CommandText = @"
            SELECT c.id, c.author_id, u.full_name AS author_name, u.username AS author_username,
                   c.body, c.is_internal, c.created_at
            FROM ticket_comments c
            LEFT JOIN users u ON u.id = c.author_id
            WHERE c.ticket_id = @id" + (isAgentOrAdmin ? "" : " AND c.is_internal = 0") + @"
            ORDER BY c.created_at ASC, c.id ASC;";
        cCmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            comments.Add(MapCommentRow(reader));
    }

    var history = new List<object>();
    await using (var hCmd = conn.CreateCommand())
    {
        hCmd.CommandText = @"
            SELECT h.id, h.actor_id, u.full_name AS actor_name, h.field_changed, h.old_value, h.new_value, h.created_at
            FROM ticket_history h
            LEFT JOIN users u ON u.id = h.actor_id
            WHERE h.ticket_id = @id
            ORDER BY h.created_at ASC, h.id ASC;";
        hCmd.Parameters.AddWithValue("@id", id);
        await using var reader = await hCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            history.Add(new
            {
                id = Convert.ToInt32(reader["id"]),
                actorId = reader["actor_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["actor_id"]),
                actorName = reader["actor_name"] is DBNull ? "System" : (string)reader["actor_name"],
                fieldChanged = (string)reader["field_changed"],
                oldValue = reader["old_value"] is DBNull ? null : (string)reader["old_value"],
                newValue = reader["new_value"] is DBNull ? null : (string)reader["new_value"],
                createdAt = Convert.ToDateTime(reader["created_at"]),
            });
        }
    }

    var relatedTickets = new List<object>();
    await using (var lCmd = conn.CreateCommand())
    {
        lCmd.CommandText = @"
            SELECT rt.id, rt.ticket_number, rt.subject, rt.status, rt.priority
            FROM ticket_links l
            JOIN tickets rt ON rt.id = l.related_ticket_id
            WHERE l.ticket_id = @id
            ORDER BY rt.created_at DESC;";
        lCmd.Parameters.AddWithValue("@id", id);
        await using var reader = await lCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            relatedTickets.Add(new
            {
                id = Convert.ToInt32(reader["id"]),
                ticketNumber = reader["ticket_number"] is DBNull ? null : (string)reader["ticket_number"],
                subject = (string)reader["subject"],
                status = (string)reader["status"],
                priority = (string)reader["priority"],
            });
        }
    }

    return Results.Ok(new { ticket, comments, history, relatedTickets });
});

tickets.MapPost("/", async (CreateTicketRequest request, HttpContext http, MySqlConnectionFactory db) =>
{
    if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Description) || string.IsNullOrWhiteSpace(request.Category))
        return Results.BadRequest(new { error = "Subject, description, and category are required." });

    var priority = string.IsNullOrWhiteSpace(request.Priority) ? "medium" : request.Priority.Trim().ToLowerInvariant();
    if (!validTicketPriorities.Contains(priority))
        return Results.BadRequest(new { error = "Priority must be low, medium, high, or critical." });

    var role = http.User.FindFirstValue(ClaimTypes.Role);
    var currentUserId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var isAgentOrAdmin = role == "admin" || role == "agent";

    // Requesters can only file tickets for themselves. Agents/admins can file on someone else's
    // behalf (e.g. a phone call from a requester who can't log in themselves).
    var requesterId = currentUserId;
    if (isAgentOrAdmin && request.RequesterId is int requestedFor)
        requesterId = requestedFor;

    await using var conn = await db.OpenAsync();
    var policies = await GetSlaPoliciesAsync(conn);
    if (!policies.TryGetValue(priority, out var policy))
        return Results.Json(new { error = "No SLA policy is configured for that priority yet." }, statusCode: 500);

    await using (var checkReqCmd = conn.CreateCommand())
    {
        checkReqCmd.CommandText = "SELECT COUNT(*) FROM users WHERE id = @id;";
        checkReqCmd.Parameters.AddWithValue("@id", requesterId);
        var exists = Convert.ToInt32(await checkReqCmd.ExecuteScalarAsync()) > 0;
        if (!exists)
            return Results.BadRequest(new { error = "The requester you specified doesn't exist." });
    }

    // Asset linking is triage work - only agents/admins can attach an asset, and only they get
    // this field on the create form at all (see README for why requesters don't get an asset picker).
    int? assetId = null;
    if (isAgentOrAdmin && request.AssetId is int reqAssetId)
    {
        await using var checkAssetCmd = conn.CreateCommand();
        checkAssetCmd.CommandText = "SELECT COUNT(*) FROM assets WHERE id = @id;";
        checkAssetCmd.Parameters.AddWithValue("@id", reqAssetId);
        var exists = Convert.ToInt32(await checkAssetCmd.ExecuteScalarAsync()) > 0;
        if (!exists)
            return Results.BadRequest(new { error = "That asset doesn't exist." });
        assetId = reqAssetId;
    }

    // Both SLA clocks start the moment the ticket is filed.
    var now = DateTime.Now;
    var responseDueAt = now.AddMinutes(policy.ResponseMinutes);
    var resolutionDueAt = now.AddMinutes(policy.ResolutionMinutes);

    await using var insertCmd = conn.CreateCommand();
    insertCmd.CommandText = @"
        INSERT INTO tickets (subject, description, category, subcategory, priority, status,
                              requester_id, department, asset_id, response_due_at, resolution_due_at)
        VALUES (@subject, @description, @category, @subcategory, @priority, 'new',
                @requesterId, @department, @assetId, @responseDueAt, @resolutionDueAt);";
    insertCmd.Parameters.AddWithValue("@subject", request.Subject.Trim());
    insertCmd.Parameters.AddWithValue("@description", request.Description.Trim());
    insertCmd.Parameters.AddWithValue("@category", request.Category.Trim());
    insertCmd.Parameters.AddWithValue("@subcategory", (object?)request.Subcategory?.Trim() ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@priority", priority);
    insertCmd.Parameters.AddWithValue("@requesterId", requesterId);
    insertCmd.Parameters.AddWithValue("@department", (object?)request.Department?.Trim() ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@assetId", (object?)assetId ?? DBNull.Value);
    insertCmd.Parameters.AddWithValue("@responseDueAt", responseDueAt);
    insertCmd.Parameters.AddWithValue("@resolutionDueAt", resolutionDueAt);
    await insertCmd.ExecuteNonQueryAsync();

    await using var idCmd = conn.CreateCommand();
    idCmd.CommandText = "SELECT LAST_INSERT_ID();";
    var newId = Convert.ToInt32(await idCmd.ExecuteScalarAsync());

    // Prefix/starting-offset are configurable from Settings -> Numbering Format (defaults: "TK-" /
    // 1000); the number itself is still always derived from the ticket's own auto-increment id, so
    // it stays guaranteed-unique no matter what an admin sets these to.
    var (numberPrefix, numberStart) = await GetNumberingConfigAsync(conn);
    await using (var numberCmd = conn.CreateCommand())
    {
        numberCmd.CommandText = "UPDATE tickets SET ticket_number = @num WHERE id = @id;";
        numberCmd.Parameters.AddWithValue("@num", $"{numberPrefix}{numberStart + newId}");
        numberCmd.Parameters.AddWithValue("@id", newId);
        await numberCmd.ExecuteNonQueryAsync();
    }

    await LogTicketHistoryAsync(conn, newId, currentUserId, "created", null, "New ticket filed");

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = TicketSelectSql + " WHERE t.id = @id;";
    fetchCmd.Parameters.AddWithValue("@id", newId);
    await using var fetchReader = await fetchCmd.ExecuteReaderAsync();
    await fetchReader.ReadAsync();

    return Results.Created($"/api/tickets/{newId}", MapTicketRow(fetchReader, policies));
});

tickets.MapPut("/{id:int}", async (int id, UpdateTicketRequest request, HttpContext http, MySqlConnectionFactory db) =>
{
    if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Description) || string.IsNullOrWhiteSpace(request.Category))
        return Results.BadRequest(new { error = "Subject, description, and category are required." });

    var priority = request.Priority?.Trim().ToLowerInvariant() ?? "";
    if (!validTicketPriorities.Contains(priority))
        return Results.BadRequest(new { error = "Priority must be low, medium, high, or critical." });

    var status = request.Status?.Trim().ToLowerInvariant() ?? "";
    if (!validTicketStatuses.Contains(status))
        return Results.BadRequest(new { error = "Not a valid status." });

    var currentUserId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    await using var conn = await db.OpenAsync();
    var policies = await GetSlaPoliciesAsync(conn);
    if (!policies.TryGetValue(priority, out var policy))
        return Results.Json(new { error = "No SLA policy is configured for that priority yet." }, statusCode: 500);

    string oldPriority, oldStatus, oldCategory;
    string? oldSubcategory, oldDepartment;
    int? oldTechnicianId, oldAssetId;
    DateTime createdAt;
    DateTime? onHoldSince, firstRespondedAt, resolvedAt, closedAt;
    int totalPausedMinutes;
    bool? responseMet, resolutionMet;
    await using (var checkCmd = conn.CreateCommand())
    {
        checkCmd.CommandText = @"
            SELECT priority, status, category, subcategory, department, assigned_technician_id, asset_id, created_at,
                   on_hold_since, total_paused_minutes, first_responded_at, response_met, resolved_at, closed_at, resolution_met
            FROM tickets WHERE id = @id LIMIT 1;";
        checkCmd.Parameters.AddWithValue("@id", id);
        await using var reader = await checkCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return Results.NotFound(new { error = "Ticket not found." });
        oldPriority = (string)reader["priority"];
        oldStatus = (string)reader["status"];
        oldCategory = (string)reader["category"];
        oldSubcategory = reader["subcategory"] is DBNull ? null : (string)reader["subcategory"];
        oldDepartment = reader["department"] is DBNull ? null : (string)reader["department"];
        oldTechnicianId = reader["assigned_technician_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["assigned_technician_id"]);
        oldAssetId = reader["asset_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["asset_id"]);
        createdAt = Convert.ToDateTime(reader["created_at"]);
        onHoldSince = reader["on_hold_since"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["on_hold_since"]);
        totalPausedMinutes = Convert.ToInt32(reader["total_paused_minutes"]);
        firstRespondedAt = reader["first_responded_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["first_responded_at"]);
        responseMet = reader["response_met"] is DBNull ? (bool?)null : Convert.ToBoolean(reader["response_met"]);
        resolvedAt = reader["resolved_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["resolved_at"]);
        closedAt = reader["closed_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["closed_at"]);
        resolutionMet = reader["resolution_met"] is DBNull ? (bool?)null : Convert.ToBoolean(reader["resolution_met"]);
    }

    if (request.AssignedTechnicianId is int techId)
    {
        await using var checkTechCmd = conn.CreateCommand();
        checkTechCmd.CommandText = "SELECT COUNT(*) FROM users WHERE id = @id;";
        checkTechCmd.Parameters.AddWithValue("@id", techId);
        var exists = Convert.ToInt32(await checkTechCmd.ExecuteScalarAsync()) > 0;
        if (!exists)
            return Results.BadRequest(new { error = "The technician you're assigning doesn't exist." });
    }

    if (request.AssetId is int assetIdCheck)
    {
        await using var checkAssetCmd = conn.CreateCommand();
        checkAssetCmd.CommandText = "SELECT COUNT(*) FROM assets WHERE id = @id;";
        checkAssetCmd.Parameters.AddWithValue("@id", assetIdCheck);
        var exists = Convert.ToInt32(await checkAssetCmd.ExecuteScalarAsync()) > 0;
        if (!exists)
            return Results.BadRequest(new { error = "That asset doesn't exist." });
    }

    var now = DateTime.Now;
    var wasOnHold = oldStatus == "on_hold";
    var isNowOnHold = status == "on_hold";

    // Pause/resume bookkeeping: entering On Hold freezes both clocks at their current remaining
    // time (by recording when the pause started); leaving On Hold banks the elapsed pause time so
    // both due dates get pushed forward by exactly how long the ticket sat paused.
    if (!wasOnHold && isNowOnHold)
    {
        onHoldSince = now;
    }
    else if (wasOnHold && !isNowOnHold)
    {
        if (onHoldSince is DateTime pausedAt)
            totalPausedMinutes += (int)Math.Max(0, (now - pausedAt).TotalMinutes);
        onHoldSince = null;
    }

    // Both due dates are always recomputed from scratch: filing time + this priority's allotted
    // minutes + however much pause time has accumulated over the ticket's life. That means a
    // priority change mid-ticket shifts the deadline, and every pause pushes it back further -
    // but nothing here resets the clock to "now".
    var responseDueAt = createdAt.AddMinutes(policy.ResponseMinutes).AddMinutes(totalPausedMinutes);
    var resolutionDueAt = createdAt.AddMinutes(policy.ResolutionMinutes).AddMinutes(totalPausedMinutes);

    // First response is a one-time fact: it's set the moment a ticket first leaves "New" via
    // triage, and never cleared again (even on reopen - the first response already happened).
    if (firstRespondedAt is null && oldStatus == "new" && status != "new")
    {
        firstRespondedAt = now;
        responseMet = now <= responseDueAt;
    }

    // Resolution is also one-time going forward, but reopening (leaving resolved/closed) clears
    // it so the ticket can earn a fresh resolvedAt/resolutionMet if it's resolved again.
    var wasTerminal = oldStatus is "resolved" or "closed";
    var isTerminal = status is "resolved" or "closed";
    if (!wasTerminal && isTerminal)
    {
        resolvedAt = now;
        resolutionMet = now <= resolutionDueAt;
    }
    else if (wasTerminal && !isTerminal)
    {
        resolvedAt = null;
        resolutionMet = null;
    }

    closedAt = status == "closed" ? (closedAt ?? now) : null;

    await using (var updateCmd = conn.CreateCommand())
    {
        updateCmd.CommandText = @"
            UPDATE tickets
            SET subject = @subject, description = @description, category = @category, subcategory = @subcategory,
                priority = @priority, status = @status, department = @department,
                assigned_technician_id = @technicianId, asset_id = @assetId, resolution_notes = @resolutionNotes,
                response_due_at = @responseDueAt, resolution_due_at = @resolutionDueAt,
                first_responded_at = @firstRespondedAt, response_met = @responseMet,
                resolved_at = @resolvedAt, resolution_met = @resolutionMet, closed_at = @closedAt,
                on_hold_since = @onHoldSince, total_paused_minutes = @totalPausedMinutes
            WHERE id = @id;";
        updateCmd.Parameters.AddWithValue("@subject", request.Subject.Trim());
        updateCmd.Parameters.AddWithValue("@description", request.Description.Trim());
        updateCmd.Parameters.AddWithValue("@category", request.Category.Trim());
        updateCmd.Parameters.AddWithValue("@subcategory", (object?)request.Subcategory?.Trim() ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@priority", priority);
        updateCmd.Parameters.AddWithValue("@status", status);
        updateCmd.Parameters.AddWithValue("@department", (object?)request.Department?.Trim() ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@technicianId", (object?)request.AssignedTechnicianId ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@assetId", (object?)request.AssetId ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@resolutionNotes", (object?)request.ResolutionNotes?.Trim() ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@responseDueAt", responseDueAt);
        updateCmd.Parameters.AddWithValue("@resolutionDueAt", resolutionDueAt);
        updateCmd.Parameters.AddWithValue("@firstRespondedAt", (object?)firstRespondedAt ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@responseMet", (object?)responseMet ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@resolvedAt", (object?)resolvedAt ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@resolutionMet", (object?)resolutionMet ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@closedAt", (object?)closedAt ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@onHoldSince", (object?)onHoldSince ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@totalPausedMinutes", totalPausedMinutes);
        updateCmd.Parameters.AddWithValue("@id", id);
        await updateCmd.ExecuteNonQueryAsync();
    }

    var newSubcategory = request.Subcategory?.Trim();
    var newDepartment = request.Department?.Trim();
    if (oldPriority != priority) await LogTicketHistoryAsync(conn, id, currentUserId, "priority", oldPriority, priority);
    if (oldStatus != status) await LogTicketHistoryAsync(conn, id, currentUserId, "status", oldStatus, status);
    if (oldCategory != request.Category.Trim()) await LogTicketHistoryAsync(conn, id, currentUserId, "category", oldCategory, request.Category.Trim());
    if ((oldSubcategory ?? "") != (newSubcategory ?? "")) await LogTicketHistoryAsync(conn, id, currentUserId, "subcategory", oldSubcategory, newSubcategory);
    if ((oldDepartment ?? "") != (newDepartment ?? "")) await LogTicketHistoryAsync(conn, id, currentUserId, "department", oldDepartment, newDepartment);
    if (oldTechnicianId != request.AssignedTechnicianId) await LogTicketHistoryAsync(conn, id, currentUserId, "assigned_technician", oldTechnicianId?.ToString(), request.AssignedTechnicianId?.ToString());
    if (oldAssetId != request.AssetId) await LogTicketHistoryAsync(conn, id, currentUserId, "asset", oldAssetId?.ToString(), request.AssetId?.ToString());

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = TicketSelectSql + " WHERE t.id = @id;";
    fetchCmd.Parameters.AddWithValue("@id", id);
    await using var fetchReader = await fetchCmd.ExecuteReaderAsync();
    await fetchReader.ReadAsync();

    return Results.Ok(MapTicketRow(fetchReader, policies));
}).RequireAuthorization("AgentOrAdmin");

tickets.MapPost("/{id:int}/comments", async (int id, CommentRequest request, HttpContext http, MySqlConnectionFactory db) =>
{
    if (string.IsNullOrWhiteSpace(request.Body))
        return Results.BadRequest(new { error = "Comment can't be empty." });

    var role = http.User.FindFirstValue(ClaimTypes.Role);
    var currentUserId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var isAgentOrAdmin = role == "admin" || role == "agent";

    await using var conn = await db.OpenAsync();

    DateTime? firstRespondedAt;
    DateTime? responseDueAt;
    await using (var checkCmd = conn.CreateCommand())
    {
        checkCmd.CommandText = "SELECT requester_id, first_responded_at, response_due_at FROM tickets WHERE id = @id LIMIT 1;";
        checkCmd.Parameters.AddWithValue("@id", id);
        await using var reader = await checkCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return Results.NotFound(new { error = "Ticket not found." });
        var requesterId = reader["requester_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["requester_id"]);
        if (!isAgentOrAdmin && requesterId != currentUserId)
            return Results.Json(new { error = "You're not authorized to comment on this ticket." }, statusCode: 403);
        firstRespondedAt = reader["first_responded_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["first_responded_at"]);
        responseDueAt = reader["response_due_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["response_due_at"]);
    }

    // Only agents/admins can mark a comment internal - a requester's comment is always public.
    var isInternal = isAgentOrAdmin && request.IsInternal;

    // A technician's/admin's first comment on a ticket counts as the first response, same as
    // triaging it out of "New" does - whichever happens first wins, and it's never reset.
    if (isAgentOrAdmin && firstRespondedAt is null)
    {
        var now = DateTime.Now;
        await using var respondCmd = conn.CreateCommand();
        respondCmd.CommandText = @"
            UPDATE tickets
            SET first_responded_at = @now, response_met = @responseMet
            WHERE id = @id;";
        respondCmd.Parameters.AddWithValue("@now", now);
        respondCmd.Parameters.AddWithValue("@responseMet", responseDueAt is null ? DBNull.Value : (object)(now <= responseDueAt));
        respondCmd.Parameters.AddWithValue("@id", id);
        await respondCmd.ExecuteNonQueryAsync();
    }

    await using (var insertCmd = conn.CreateCommand())
    {
        insertCmd.CommandText = @"
            INSERT INTO ticket_comments (ticket_id, author_id, body, is_internal)
            VALUES (@ticketId, @authorId, @body, @isInternal);";
        insertCmd.Parameters.AddWithValue("@ticketId", id);
        insertCmd.Parameters.AddWithValue("@authorId", currentUserId);
        insertCmd.Parameters.AddWithValue("@body", request.Body.Trim());
        insertCmd.Parameters.AddWithValue("@isInternal", isInternal);
        await insertCmd.ExecuteNonQueryAsync();
    }

    await using var idCmd = conn.CreateCommand();
    idCmd.CommandText = "SELECT LAST_INSERT_ID();";
    var newId = Convert.ToInt32(await idCmd.ExecuteScalarAsync());

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = @"
        SELECT c.id, c.author_id, u.full_name AS author_name, u.username AS author_username,
               c.body, c.is_internal, c.created_at
        FROM ticket_comments c
        LEFT JOIN users u ON u.id = c.author_id
        WHERE c.id = @id;";
    fetchCmd.Parameters.AddWithValue("@id", newId);
    await using var fetchReader = await fetchCmd.ExecuteReaderAsync();
    await fetchReader.ReadAsync();

    return Results.Created($"/api/tickets/{id}/comments/{newId}", MapCommentRow(fetchReader));
});

tickets.MapPost("/{id:int}/reopen", async (int id, HttpContext http, MySqlConnectionFactory db) =>
{
    var role = http.User.FindFirstValue(ClaimTypes.Role);
    var currentUserId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var isAgentOrAdmin = role == "admin" || role == "agent";

    await using var conn = await db.OpenAsync();
    var policies = await GetSlaPoliciesAsync(conn);

    string status;
    int? requesterId;
    await using (var checkCmd = conn.CreateCommand())
    {
        checkCmd.CommandText = "SELECT status, requester_id FROM tickets WHERE id = @id LIMIT 1;";
        checkCmd.Parameters.AddWithValue("@id", id);
        await using var reader = await checkCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return Results.NotFound(new { error = "Ticket not found." });
        status = (string)reader["status"];
        requesterId = reader["requester_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["requester_id"]);
    }

    if (!isAgentOrAdmin && requesterId != currentUserId)
        return Results.Json(new { error = "You're not authorized to reopen this ticket." }, statusCode: 403);

    if (status != "resolved" && status != "closed")
        return Results.BadRequest(new { error = "Only resolved or closed tickets can be reopened." });

    // Reopening only clears the resolution clock's outcome - first response already happened
    // historically and stays on the record even after the ticket comes back to life.
    await using (var updateCmd = conn.CreateCommand())
    {
        updateCmd.CommandText = @"
            UPDATE tickets
            SET status = 'in_progress', resolved_at = NULL, closed_at = NULL, resolution_met = NULL
            WHERE id = @id;";
        updateCmd.Parameters.AddWithValue("@id", id);
        await updateCmd.ExecuteNonQueryAsync();
    }

    await LogTicketHistoryAsync(conn, id, currentUserId, "status", status, "in_progress (reopened)");

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = TicketSelectSql + " WHERE t.id = @id;";
    fetchCmd.Parameters.AddWithValue("@id", id);
    await using var fetchReader = await fetchCmd.ExecuteReaderAsync();
    await fetchReader.ReadAsync();

    return Results.Ok(MapTicketRow(fetchReader, policies));
});

tickets.MapPost("/{id:int}/links", async (int id, LinkRequest request, MySqlConnectionFactory db) =>
{
    if (request.RelatedTicketId == id)
        return Results.BadRequest(new { error = "A ticket can't be related to itself." });

    await using var conn = await db.OpenAsync();

    await using (var checkCmd = conn.CreateCommand())
    {
        checkCmd.CommandText = "SELECT COUNT(*) FROM tickets WHERE id IN (@id, @relatedId);";
        checkCmd.Parameters.AddWithValue("@id", id);
        checkCmd.Parameters.AddWithValue("@relatedId", request.RelatedTicketId);
        var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
        if (count < 2)
            return Results.BadRequest(new { error = "One of those tickets doesn't exist." });
    }

    // Stored both directions so either ticket's detail view shows the relationship.
    await using (var insertCmd1 = conn.CreateCommand())
    {
        insertCmd1.CommandText = "INSERT IGNORE INTO ticket_links (ticket_id, related_ticket_id) VALUES (@id, @relatedId);";
        insertCmd1.Parameters.AddWithValue("@id", id);
        insertCmd1.Parameters.AddWithValue("@relatedId", request.RelatedTicketId);
        await insertCmd1.ExecuteNonQueryAsync();
    }
    await using (var insertCmd2 = conn.CreateCommand())
    {
        insertCmd2.CommandText = "INSERT IGNORE INTO ticket_links (ticket_id, related_ticket_id) VALUES (@id, @relatedId);";
        insertCmd2.Parameters.AddWithValue("@id", request.RelatedTicketId);
        insertCmd2.Parameters.AddWithValue("@relatedId", id);
        await insertCmd2.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { message = "Tickets linked." });
}).RequireAuthorization("AgentOrAdmin");

tickets.MapDelete("/{id:int}/links/{relatedId:int}", async (int id, int relatedId, MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    await using (var cmd1 = conn.CreateCommand())
    {
        cmd1.CommandText = "DELETE FROM ticket_links WHERE ticket_id = @id AND related_ticket_id = @relatedId;";
        cmd1.Parameters.AddWithValue("@id", id);
        cmd1.Parameters.AddWithValue("@relatedId", relatedId);
        await cmd1.ExecuteNonQueryAsync();
    }
    await using (var cmd2 = conn.CreateCommand())
    {
        cmd2.CommandText = "DELETE FROM ticket_links WHERE ticket_id = @id AND related_ticket_id = @relatedId;";
        cmd2.Parameters.AddWithValue("@id", relatedId);
        cmd2.Parameters.AddWithValue("@relatedId", id);
        await cmd2.ExecuteNonQueryAsync();
    }
    return Results.Ok(new { message = "Link removed." });
}).RequireAuthorization("AgentOrAdmin");

// ---------------------------------------------------------------------------
// SLA administration (agents + admins can view; only admins can edit the rules) - backs the
// Settings -> SLA Rules editor and the Reports -> SLA Compliance page.
// ---------------------------------------------------------------------------
var sla = app.MapGroup("/api/sla").RequireAuthorization("AgentOrAdmin");

const string SlaPolicySelectSql = @"
    SELECT priority, response_minutes, resolution_minutes, updated_at
    FROM sla_policies
    ORDER BY FIELD(priority, 'critical', 'high', 'medium', 'low');";

sla.MapGet("/policies", async (MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = SlaPolicySelectSql;

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        list.Add(MapSlaPolicyRow(reader));

    return Results.Ok(list);
});

sla.MapPut("/policies", async (UpdateSlaPoliciesRequest request, MySqlConnectionFactory db) =>
{
    if (request.Policies is null || request.Policies.Count == 0)
        return Results.BadRequest(new { error = "At least one SLA rule is required." });

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in request.Policies)
    {
        var priority = p.Priority?.Trim().ToLowerInvariant() ?? "";
        if (!validTicketPriorities.Contains(priority))
            return Results.BadRequest(new { error = $"'{p.Priority}' isn't a valid priority." });
        if (!seen.Add(priority))
            return Results.BadRequest(new { error = $"'{priority}' was listed more than once." });
        if (p.ResponseMinutes <= 0 || p.ResolutionMinutes <= 0)
            return Results.BadRequest(new { error = "Response and resolution times must be greater than zero minutes." });
        if (p.ResponseMinutes > p.ResolutionMinutes)
            return Results.BadRequest(new { error = $"{priority}: response time can't be longer than resolution time." });
    }

    await using var conn = await db.OpenAsync();
    foreach (var p in request.Policies)
    {
        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = @"
            UPDATE sla_policies
            SET response_minutes = @responseMinutes, resolution_minutes = @resolutionMinutes
            WHERE priority = @priority;";
        updateCmd.Parameters.AddWithValue("@responseMinutes", p.ResponseMinutes);
        updateCmd.Parameters.AddWithValue("@resolutionMinutes", p.ResolutionMinutes);
        updateCmd.Parameters.AddWithValue("@priority", p.Priority.Trim().ToLowerInvariant());
        await updateCmd.ExecuteNonQueryAsync();
    }

    await using var fetchCmd = conn.CreateCommand();
    fetchCmd.CommandText = SlaPolicySelectSql;
    var list = new List<object>();
    await using var fetchReader = await fetchCmd.ExecuteReaderAsync();
    while (await fetchReader.ReadAsync())
        list.Add(MapSlaPolicyRow(fetchReader));

    return Results.Ok(list);
}).RequireAuthorization("AdminOnly");

sla.MapGet("/report", async (MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT priority, status, first_responded_at, response_due_at, response_met,
               resolution_due_at, resolution_met, on_hold_since
        FROM tickets;";

    var now = DateTime.Now;
    var overall = new SlaReportBucket();
    var byPriority = new Dictionary<string, SlaReportBucket>(StringComparer.OrdinalIgnoreCase);

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var priority = (string)reader["priority"];
        var status = (string)reader["status"];
        var firstRespondedAt = reader["first_responded_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["first_responded_at"]);
        var responseDueAt = reader["response_due_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["response_due_at"]);
        var responseMet = reader["response_met"] is DBNull ? (bool?)null : Convert.ToBoolean(reader["response_met"]);
        var resolutionDueAt = reader["resolution_due_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["resolution_due_at"]);
        var resolutionMet = reader["resolution_met"] is DBNull ? (bool?)null : Convert.ToBoolean(reader["resolution_met"]);
        var onHoldSince = reader["on_hold_since"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["on_hold_since"]);

        if (!byPriority.TryGetValue(priority, out var bucket))
        {
            bucket = new SlaReportBucket();
            byPriority[priority] = bucket;
        }

        var isTerminal = status is "resolved" or "closed";
        var isOnHold = status == "on_hold";
        // Same "frozen while paused" reference point as the live ticket view, so a report run
        // mid-pause doesn't count a paused ticket as newly breached just because time passed.
        var effectiveNow = isOnHold && onHoldSince is DateTime pausedAt ? pausedAt : now;

        bucket.Total++; overall.Total++;

        if (responseMet == true) { bucket.ResponseMet++; overall.ResponseMet++; }
        else if (responseMet == false) { bucket.ResponseBreached++; overall.ResponseBreached++; }
        else if (firstRespondedAt is null)
        {
            bucket.ResponsePending++; overall.ResponsePending++;
            if (responseDueAt is DateTime rd && effectiveNow > rd) { bucket.ResponseCurrentlyBreached++; overall.ResponseCurrentlyBreached++; }
        }

        if (resolutionMet == true) { bucket.ResolutionMet++; overall.ResolutionMet++; }
        else if (resolutionMet == false) { bucket.ResolutionBreached++; overall.ResolutionBreached++; }
        else if (!isTerminal)
        {
            bucket.ResolutionPending++; overall.ResolutionPending++;
            if (resolutionDueAt is DateTime rd2 && effectiveNow > rd2) { bucket.ResolutionCurrentlyBreached++; overall.ResolutionCurrentlyBreached++; }
        }
    }

    var priorityOrder = new[] { "critical", "high", "medium", "low" };
    var byPriorityJson = priorityOrder
        .Where(byPriority.ContainsKey)
        .Select(p => MapSlaReportBucket(p, byPriority[p]))
        .ToList();

    return Results.Ok(new
    {
        generatedAt = now,
        overall = MapSlaReportBucket("overall", overall),
        byPriority = byPriorityJson,
    });
});

// ---------------------------------------------------------------------------
// Settings - system configuration (agents can view most of it since they need the lookup
// lists day-to-day; only admins can change anything). Everything living, load-bearing ENUM
// (ticket priority/status, user role) is deliberately NOT here - see README for why those
// stay fixed instead of becoming freely editable settings.
// ---------------------------------------------------------------------------

// Simple name + active-flag lookup tables (Departments, Locations, Ticket Categories, Asset
// Categories) all share the same CRUD shape, so one helper wires up all four instead of
// hand-rolling four nearly-identical route groups. These are plain pick-lists, not foreign
// keys - tickets/assets store the chosen name as free text, so deleting a lookup entry never
// orphans an existing record; it just stops showing up as a dropdown option going forward.
void MapLookupGroup(string routePath, string tableName)
{
    var group = app.MapGroup(routePath).RequireAuthorization("AgentOrAdmin");

    group.MapGet("/", async (MySqlConnectionFactory db) =>
    {
        await using var conn = await db.OpenAsync();
        return Results.Ok(await GetLookupListAsync(conn, tableName));
    });

    group.MapPost("/", async (LookupItemRequest request, MySqlConnectionFactory db) =>
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "Name is required." });

        await using var conn = await db.OpenAsync();
        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = $"INSERT INTO {tableName} (name, is_active) VALUES (@name, @isActive);";
        insertCmd.Parameters.AddWithValue("@name", request.Name.Trim());
        insertCmd.Parameters.AddWithValue("@isActive", request.IsActive ?? true);

        try
        {
            await insertCmd.ExecuteNonQueryAsync();
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return Results.Json(new { error = "That name already exists." }, statusCode: 409);
        }

        await using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT LAST_INSERT_ID();";
        var newId = Convert.ToInt32(await idCmd.ExecuteScalarAsync());

        return Results.Created($"{routePath}/{newId}", new { id = newId, name = request.Name.Trim(), isActive = request.IsActive ?? true });
    }).RequireAuthorization("AdminOnly");

    group.MapPut("/{id:int}", async (int id, LookupItemRequest request, MySqlConnectionFactory db) =>
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "Name is required." });

        await using var conn = await db.OpenAsync();

        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE id = @id;";
            checkCmd.Parameters.AddWithValue("@id", id);
            var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;
            if (!exists)
                return Results.NotFound(new { error = "Not found." });
        }

        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = $"UPDATE {tableName} SET name = @name, is_active = @isActive WHERE id = @id;";
        updateCmd.Parameters.AddWithValue("@name", request.Name.Trim());
        updateCmd.Parameters.AddWithValue("@isActive", request.IsActive ?? true);
        updateCmd.Parameters.AddWithValue("@id", id);

        try
        {
            await updateCmd.ExecuteNonQueryAsync();
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return Results.Json(new { error = "That name already exists." }, statusCode: 409);
        }

        return Results.Ok(new { id, name = request.Name.Trim(), isActive = request.IsActive ?? true });
    }).RequireAuthorization("AdminOnly");

    group.MapDelete("/{id:int}", async (int id, MySqlConnectionFactory db) =>
    {
        await using var conn = await db.OpenAsync();
        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = $"DELETE FROM {tableName} WHERE id = @id;";
        deleteCmd.Parameters.AddWithValue("@id", id);
        var rows = await deleteCmd.ExecuteNonQueryAsync();
        if (rows == 0)
            return Results.NotFound(new { error = "Not found." });
        return Results.Ok(new { message = "Deleted." });
    }).RequireAuthorization("AdminOnly");
}

MapLookupGroup("/api/settings/departments", "departments");
MapLookupGroup("/api/settings/locations", "locations");
MapLookupGroup("/api/settings/ticket-categories", "ticket_categories");
MapLookupGroup("/api/settings/asset-categories", "asset_categories");

// Holidays - simple name + date list. No PUT: editing a holiday is delete-and-recreate from
// the UI, which keeps this endpoint (and the frontend table) simple.
var holidays = app.MapGroup("/api/settings/holidays").RequireAuthorization("AgentOrAdmin");

holidays.MapGet("/", async (MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, name, holiday_date, created_at FROM holidays ORDER BY holiday_date ASC;";
    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            id = Convert.ToInt32(reader["id"]),
            name = (string)reader["name"],
            date = Convert.ToDateTime(reader["holiday_date"]),
            createdAt = Convert.ToDateTime(reader["created_at"]),
        });
    }
    return Results.Ok(list);
});

holidays.MapPost("/", async (CreateHolidayRequest request, MySqlConnectionFactory db) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Name is required." });

    await using var conn = await db.OpenAsync();
    await using var insertCmd = conn.CreateCommand();
    insertCmd.CommandText = "INSERT INTO holidays (name, holiday_date) VALUES (@name, @date);";
    insertCmd.Parameters.AddWithValue("@name", request.Name.Trim());
    insertCmd.Parameters.AddWithValue("@date", request.Date.Date);
    await insertCmd.ExecuteNonQueryAsync();

    await using var idCmd = conn.CreateCommand();
    idCmd.CommandText = "SELECT LAST_INSERT_ID();";
    var newId = Convert.ToInt32(await idCmd.ExecuteScalarAsync());

    return Results.Created($"/api/settings/holidays/{newId}", new { id = newId, name = request.Name.Trim(), date = request.Date.Date });
}).RequireAuthorization("AdminOnly");

holidays.MapDelete("/{id:int}", async (int id, MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    await using var deleteCmd = conn.CreateCommand();
    deleteCmd.CommandText = "DELETE FROM holidays WHERE id = @id;";
    deleteCmd.Parameters.AddWithValue("@id", id);
    var rows = await deleteCmd.ExecuteNonQueryAsync();
    if (rows == 0)
        return Results.NotFound(new { error = "Holiday not found." });
    return Results.Ok(new { message = "Holiday deleted." });
}).RequireAuthorization("AdminOnly");

// Business hours - one fixed row per day of week (0 = Sunday .. 6 = Saturday, matching
// JavaScript's Date.getDay()). Stored and returned today; not yet consulted by the SLA due-date
// math (see README) - due dates still run on wall-clock minutes, 24/7.
var businessHours = app.MapGroup("/api/settings/business-hours").RequireAuthorization("AgentOrAdmin");

businessHours.MapGet("/", async (MySqlConnectionFactory db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(await GetBusinessHoursAsync(conn));
});

businessHours.MapPut("/", async (UpdateBusinessHoursRequest request, MySqlConnectionFactory db) =>
{
    if (request.Days is null || request.Days.Count == 0)
        return Results.BadRequest(new { error = "At least one day is required." });

    await using var conn = await db.OpenAsync();
    foreach (var d in request.Days)
    {
        if (d.DayOfWeek < 0 || d.DayOfWeek > 6)
            return Results.BadRequest(new { error = "dayOfWeek must be between 0 and 6." });

        TimeSpan? openTime = null, closeTime = null;
        if (d.IsOpen)
        {
            if (!TimeSpan.TryParse(d.OpenTime, out var ot) || !TimeSpan.TryParse(d.CloseTime, out var ct))
                return Results.BadRequest(new { error = $"Day {d.DayOfWeek}: a valid open and close time are required when marked open." });
            openTime = ot;
            closeTime = ct;
        }

        await using var upsertCmd = conn.CreateCommand();
        upsertCmd.CommandText = @"
            INSERT INTO business_hours (day_of_week, is_open, open_time, close_time)
            VALUES (@day, @isOpen, @openTime, @closeTime)
            ON DUPLICATE KEY UPDATE is_open = @isOpen, open_time = @openTime, close_time = @closeTime;";
        upsertCmd.Parameters.AddWithValue("@day", d.DayOfWeek);
        upsertCmd.Parameters.AddWithValue("@isOpen", d.IsOpen);
        upsertCmd.Parameters.AddWithValue("@openTime", (object?)openTime ?? DBNull.Value);
        upsertCmd.Parameters.AddWithValue("@closeTime", (object?)closeTime ?? DBNull.Value);
        await upsertCmd.ExecuteNonQueryAsync();
    }

    return Results.Ok(await GetBusinessHoursAsync(conn));
}).RequireAuthorization("AdminOnly");

// Generic key-value config store for the free-form settings sections (Company Info, Numbering
// Format, Notifications, Email, Appearance/Theme). Each key's JSON shape is owned entirely by
// the frontend - the backend just persists whatever object it's given, except for "email" where
// the stored SMTP password is stripped from every response and only overwritten when the client
// actually submits a new, non-empty one (GET never has a real password to send back, so an
// empty field on PUT means "leave it alone," not "clear it").
var settingsConfigKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "company", "numbering", "notifications", "email", "theme" };

object StripEmailPassword(string json)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var obj = new Dictionary<string, object?>();
    foreach (var prop in root.EnumerateObject())
    {
        if (string.Equals(prop.Name, "password", StringComparison.OrdinalIgnoreCase)) continue;
        obj[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
    }
    var hasPassword = root.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(pw.GetString());
    obj["hasPassword"] = hasPassword;
    return obj;
}

var config = app.MapGroup("/api/settings/config").RequireAuthorization("AgentOrAdmin");

config.MapGet("/{key}", async (string key, MySqlConnectionFactory db) =>
{
    if (!settingsConfigKeys.Contains(key))
        return Results.NotFound(new { error = "Unknown settings key." });

    await using var conn = await db.OpenAsync();
    var value = await GetConfigValueAsync(conn, key);
    if (value is null)
        return Results.Ok(new { });

    if (string.Equals(key, "email", StringComparison.OrdinalIgnoreCase))
        return Results.Ok(StripEmailPassword(value));

    return Results.Content(value, "application/json");
});

config.MapPut("/{key}", async (string key, JsonElement body, MySqlConnectionFactory db) =>
{
    if (!settingsConfigKeys.Contains(key))
        return Results.NotFound(new { error = "Unknown settings key." });
    if (body.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "Expected a JSON object." });

    await using var conn = await db.OpenAsync();

    string valueToStore;
    if (string.Equals(key, "email", StringComparison.OrdinalIgnoreCase))
    {
        var hasNewPassword = body.TryGetProperty("password", out var pwEl) &&
            pwEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(pwEl.GetString());

        var existingPassword = "";
        var existingJson = await GetConfigValueAsync(conn, key);
        if (existingJson is not null)
        {
            using var existingDoc = JsonDocument.Parse(existingJson);
            if (existingDoc.RootElement.TryGetProperty("password", out var existingPwEl) && existingPwEl.ValueKind == JsonValueKind.String)
                existingPassword = existingPwEl.GetString() ?? "";
        }

        var merged = new Dictionary<string, object?>();
        foreach (var prop in body.EnumerateObject())
        {
            if (string.Equals(prop.Name, "password", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(prop.Name, "hasPassword", StringComparison.OrdinalIgnoreCase)) continue;
            merged[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }
        merged["password"] = hasNewPassword ? pwEl.GetString() : existingPassword;
        valueToStore = JsonSerializer.Serialize(merged);
    }
    else
    {
        valueToStore = body.GetRawText();
    }

    await SetConfigValueAsync(conn, key, valueToStore);

    return string.Equals(key, "email", StringComparison.OrdinalIgnoreCase)
        ? Results.Ok(StripEmailPassword(valueToStore))
        : Results.Content(valueToStore, "application/json");
}).RequireAuthorization("AdminOnly");

// Locally this just means "listen on port 5000" like before. On a host like Render/Railway,
// the platform picks the port itself and hands it to the app via the PORT environment variable -
// binding to 0.0.0.0 (all interfaces) instead of localhost is what makes the app reachable from
// outside the container at all.
var listenPort = Environment.GetEnvironmentVariable("PORT") ?? "5000";
app.Urls.Add($"http://0.0.0.0:{listenPort}");
app.Run();

static async Task SeedAdminAsync(MySqlConnectionFactory db, ILogger logger)
{
    await using var conn = await db.OpenAsync();

    await using (var checkCmd = conn.CreateCommand())
    {
        checkCmd.CommandText = "SELECT COUNT(*) FROM users;";
        var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
        if (count > 0) return;
    }

    var hash = BCrypt.Net.BCrypt.HashPassword("Admin@123");

    await using var insertCmd = conn.CreateCommand();
    insertCmd.CommandText = @"
        INSERT INTO users (username, email, password_hash, full_name, role, is_active)
        VALUES (@username, @email, @hash, @fullName, @role, 1);";
    insertCmd.Parameters.AddWithValue("@username", "admin");
    insertCmd.Parameters.AddWithValue("@email", "admin@deskflow.local");
    insertCmd.Parameters.AddWithValue("@hash", hash);
    insertCmd.Parameters.AddWithValue("@fullName", "DeskFlow Admin");
    insertCmd.Parameters.AddWithValue("@role", "admin");
    await insertCmd.ExecuteNonQueryAsync();

    logger.LogWarning("Seeded default admin user -> username: admin / password: Admin@123. Change this after your first login.");
}

// Encrypts a JSON-serializable payload with AES-256-GCM and returns it as a plain numeric JSON
// array (nonce + ciphertext + auth tag, all concatenated) - the same shape as a raw byte array,
// which is what shows up as an unreadable list of numbers in the browser's Network tab instead
// of a plain JSON object.
static IResult EncryptedResult(object payload, byte[] key, int statusCode = 200)
{
    var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

    var nonce = RandomNumberGenerator.GetBytes(12);
    var ciphertext = new byte[plaintext.Length];
    var tag = new byte[16];

    using var aes = new AesGcm(key, 16);
    aes.Encrypt(nonce, plaintext, ciphertext, tag);

    var combined = new byte[nonce.Length + ciphertext.Length + tag.Length];
    Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
    Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length, ciphertext.Length);
    Buffer.BlockCopy(tag, 0, combined, nonce.Length + ciphertext.Length, tag.Length);

    return Results.Json(Array.ConvertAll(combined, b => (int)b), statusCode: statusCode);
}

static object MapUserRow(MySqlDataReader reader) => new
{
    id = Convert.ToInt32(reader["id"]),
    username = (string)reader["username"],
    email = (string)reader["email"],
    fullName = (string)reader["full_name"],
    role = (string)reader["role"],
    isActive = Convert.ToBoolean(reader["is_active"]),
    createdAt = Convert.ToDateTime(reader["created_at"]),
    updatedAt = Convert.ToDateTime(reader["updated_at"]),
};

static object MapAssetRow(MySqlDataReader reader) => new
{
    id = Convert.ToInt32(reader["id"]),
    assetTag = (string)reader["asset_tag"],
    name = (string)reader["name"],
    type = (string)reader["type"],
    serialNumber = reader["serial_number"] is DBNull ? null : (string)reader["serial_number"],
    status = (string)reader["status"],
    assignedToId = reader["assigned_to_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["assigned_to_id"]),
    assignedToName = reader["assigned_to_name"] is DBNull ? null : (string)reader["assigned_to_name"],
    assignedToUsername = reader["assigned_to_username"] is DBNull ? null : (string)reader["assigned_to_username"],
    department = reader["department"] is DBNull ? null : (string)reader["department"],
    location = reader["location"] is DBNull ? null : (string)reader["location"],
    purchasedAt = reader["purchased_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["purchased_at"]),
    warrantyExpiresAt = reader["warranty_expires_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["warranty_expires_at"]),
    notes = reader["notes"] is DBNull ? null : (string)reader["notes"],
    createdAt = Convert.ToDateTime(reader["created_at"]),
    updatedAt = Convert.ToDateTime(reader["updated_at"]),
};

static object MapTicketRow(MySqlDataReader reader, Dictionary<string, (int ResponseMinutes, int ResolutionMinutes)> policies)
{
    var priority = (string)reader["priority"];
    var status = (string)reader["status"];

    var responseDueAt = reader["response_due_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["response_due_at"]);
    var resolutionDueAt = reader["resolution_due_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["resolution_due_at"]);
    var firstRespondedAt = reader["first_responded_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["first_responded_at"]);
    var responseMet = reader["response_met"] is DBNull ? (bool?)null : Convert.ToBoolean(reader["response_met"]);
    var resolutionMet = reader["resolution_met"] is DBNull ? (bool?)null : Convert.ToBoolean(reader["resolution_met"]);
    var onHoldSince = reader["on_hold_since"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["on_hold_since"]);
    var totalPausedMinutes = Convert.ToInt32(reader["total_paused_minutes"]);

    var isTerminal = status is "resolved" or "closed";
    var isOnHold = status == "on_hold";

    // Which clock is "live" right now: still waiting on a first response, chasing resolution,
    // or done (ticket closed out, so we just show the final compliance verdict instead of a timer).
    var slaPhase = isTerminal ? "done" : firstRespondedAt is null ? "response" : "resolution";
    DateTime? slaDueAt = slaPhase switch
    {
        "response" => responseDueAt,
        "resolution" => resolutionDueAt,
        _ => null,
    };

    // While paused, remaining time is measured against the moment the pause started (not "now"),
    // so the countdown visibly freezes instead of continuing to tick down during the hold.
    var effectiveNow = isOnHold && onHoldSince is DateTime pausedAt ? pausedAt : DateTime.Now;

    long? slaRemainingSeconds = slaDueAt is DateTime due ? (long)Math.Round((due - effectiveNow).TotalSeconds) : null;
    var isSlaBreached = slaPhase != "done" && slaRemainingSeconds is long rs0 && rs0 < 0;

    // "At risk" scales with the SLA itself instead of a flat cutoff - a 15-minute Critical response
    // clock and a 24-hour Low resolution clock can't share the same "2 hours left" warning threshold.
    int? phaseTotalMinutes = policies.TryGetValue(priority, out var policy)
        ? slaPhase switch { "response" => policy.ResponseMinutes, "resolution" => policy.ResolutionMinutes, _ => (int?)null }
        : null;
    var isSlaAtRisk = slaPhase != "done" && !isSlaBreached
        && slaRemainingSeconds is long rs1 && phaseTotalMinutes is int totalMin && totalMin > 0
        && rs1 <= totalMin * 60 * 0.2;

    return new
    {
        id = Convert.ToInt32(reader["id"]),
        ticketNumber = reader["ticket_number"] is DBNull ? null : (string)reader["ticket_number"],
        subject = (string)reader["subject"],
        description = (string)reader["description"],
        category = (string)reader["category"],
        subcategory = reader["subcategory"] is DBNull ? null : (string)reader["subcategory"],
        priority,
        status,
        requesterId = reader["requester_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["requester_id"]),
        requesterName = reader["requester_name"] is DBNull ? null : (string)reader["requester_name"],
        requesterUsername = reader["requester_username"] is DBNull ? null : (string)reader["requester_username"],
        assignedTechnicianId = reader["assigned_technician_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["assigned_technician_id"]),
        assignedTechnicianName = reader["technician_name"] is DBNull ? null : (string)reader["technician_name"],
        assignedTechnicianUsername = reader["technician_username"] is DBNull ? null : (string)reader["technician_username"],
        department = reader["department"] is DBNull ? null : (string)reader["department"],
        assetId = reader["asset_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["asset_id"]),
        assetTag = reader["asset_tag"] is DBNull ? null : (string)reader["asset_tag"],
        assetName = reader["asset_name"] is DBNull ? null : (string)reader["asset_name"],
        resolutionNotes = reader["resolution_notes"] is DBNull ? null : (string)reader["resolution_notes"],
        resolvedAt = reader["resolved_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["resolved_at"]),
        closedAt = reader["closed_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(reader["closed_at"]),

        // SLA engine fields.
        slaPhase,
        slaDueAt,
        slaRemainingSeconds,
        isSlaOnHold = isOnHold,
        isSlaBreached,
        isSlaAtRisk,
        responseDueAt,
        resolutionDueAt,
        firstRespondedAt,
        responseMet,
        resolutionMet,
        onHoldSince,
        totalPausedMinutes,

        createdAt = Convert.ToDateTime(reader["created_at"]),
        updatedAt = Convert.ToDateTime(reader["updated_at"]),
    };
}

static object MapSlaPolicyRow(MySqlDataReader reader) => new
{
    priority = (string)reader["priority"],
    responseMinutes = Convert.ToInt32(reader["response_minutes"]),
    resolutionMinutes = Convert.ToInt32(reader["resolution_minutes"]),
    updatedAt = Convert.ToDateTime(reader["updated_at"]),
};

static object MapSlaReportBucket(string priority, SlaReportBucket b) => new
{
    priority,
    total = b.Total,
    response = new
    {
        met = b.ResponseMet,
        breached = b.ResponseBreached,
        pending = b.ResponsePending,
        currentlyBreached = b.ResponseCurrentlyBreached,
        compliancePercent = (b.ResponseMet + b.ResponseBreached) == 0
            ? (double?)null
            : Math.Round(100.0 * b.ResponseMet / (b.ResponseMet + b.ResponseBreached), 1),
    },
    resolution = new
    {
        met = b.ResolutionMet,
        breached = b.ResolutionBreached,
        pending = b.ResolutionPending,
        currentlyBreached = b.ResolutionCurrentlyBreached,
        compliancePercent = (b.ResolutionMet + b.ResolutionBreached) == 0
            ? (double?)null
            : Math.Round(100.0 * b.ResolutionMet / (b.ResolutionMet + b.ResolutionBreached), 1),
    },
};

static object MapCommentRow(MySqlDataReader reader) => new
{
    id = Convert.ToInt32(reader["id"]),
    authorId = reader["author_id"] is DBNull ? (int?)null : Convert.ToInt32(reader["author_id"]),
    authorName = reader["author_name"] is DBNull ? "Deleted user" : (string)reader["author_name"],
    authorUsername = reader["author_username"] is DBNull ? null : (string)reader["author_username"],
    body = (string)reader["body"],
    isInternal = Convert.ToBoolean(reader["is_internal"]),
    createdAt = Convert.ToDateTime(reader["created_at"]),
};

static async Task LogTicketHistoryAsync(MySqlConnection conn, int ticketId, int actorId, string fieldChanged, string? oldValue, string? newValue)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO ticket_history (ticket_id, actor_id, field_changed, old_value, new_value)
        VALUES (@ticketId, @actorId, @field, @oldValue, @newValue);";
    cmd.Parameters.AddWithValue("@ticketId", ticketId);
    cmd.Parameters.AddWithValue("@actorId", actorId);
    cmd.Parameters.AddWithValue("@field", fieldChanged);
    cmd.Parameters.AddWithValue("@oldValue", (object?)oldValue ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@newValue", (object?)newValue ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
}

// Loaded fresh on every request rather than cached - the table is tiny (one row per priority)
// and this keeps a policy edit from Settings taking effect instantly for every in-flight ticket,
// consistent with the rest of the app not caching anything.
static async Task<Dictionary<string, (int ResponseMinutes, int ResolutionMinutes)>> GetSlaPoliciesAsync(MySqlConnection conn)
{
    var policies = new Dictionary<string, (int ResponseMinutes, int ResolutionMinutes)>(StringComparer.OrdinalIgnoreCase);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT priority, response_minutes, resolution_minutes FROM sla_policies;";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var priority = (string)reader["priority"];
        policies[priority] = (Convert.ToInt32(reader["response_minutes"]), Convert.ToInt32(reader["resolution_minutes"]));
    }
    return policies;
}

static async Task<List<object>> GetLookupListAsync(MySqlConnection conn, string tableName)
{
    var list = new List<object>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT id, name, is_active, created_at, updated_at FROM {tableName} ORDER BY name ASC;";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            id = Convert.ToInt32(reader["id"]),
            name = (string)reader["name"],
            isActive = Convert.ToBoolean(reader["is_active"]),
            createdAt = Convert.ToDateTime(reader["created_at"]),
            updatedAt = Convert.ToDateTime(reader["updated_at"]),
        });
    }
    return list;
}

static async Task<List<object>> GetBusinessHoursAsync(MySqlConnection conn)
{
    var list = new List<object>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT day_of_week, is_open, open_time, close_time FROM business_hours ORDER BY day_of_week ASC;";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            dayOfWeek = Convert.ToInt32(reader["day_of_week"]),
            isOpen = Convert.ToBoolean(reader["is_open"]),
            openTime = reader["open_time"] is DBNull ? null : ((TimeSpan)reader["open_time"]).ToString(@"hh\:mm"),
            closeTime = reader["close_time"] is DBNull ? null : ((TimeSpan)reader["close_time"]).ToString(@"hh\:mm"),
        });
    }
    return list;
}

// Loaded fresh per request, same as the SLA policies - these tables are tiny and this keeps a
// Settings edit taking effect immediately without any cache invalidation to think about.
static async Task<string?> GetConfigValueAsync(MySqlConnection conn, string key)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value FROM app_settings WHERE setting_key = @key LIMIT 1;";
    cmd.Parameters.AddWithValue("@key", key.ToLowerInvariant());
    var result = await cmd.ExecuteScalarAsync();
    return result is null or DBNull ? null : (string)result;
}

static async Task SetConfigValueAsync(MySqlConnection conn, string key, string json)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO app_settings (setting_key, setting_value)
        VALUES (@key, @value)
        ON DUPLICATE KEY UPDATE setting_value = @value;";
    cmd.Parameters.AddWithValue("@key", key.ToLowerInvariant());
    cmd.Parameters.AddWithValue("@value", json);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<(string Prefix, int StartAt)> GetNumberingConfigAsync(MySqlConnection conn)
{
    var json = await GetConfigValueAsync(conn, "numbering");
    if (json is null) return ("TK-", 1000);
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var prefix = root.TryGetProperty("prefix", out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "TK-") : "TK-";
        var startAt = root.TryGetProperty("startAt", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 1000;
        return (prefix, startAt);
    }
    catch (JsonException)
    {
        return ("TK-", 1000);
    }
}

record LoginRequest(string Username, string Password);
record CreateUserRequest(string Username, string Email, string Password, string FullName, string Role, bool IsActive);
record UpdateUserRequest(string Email, string FullName, string Role, bool IsActive, string? Password);
record CreateAssetRequest(string AssetTag, string Name, string Type, string? SerialNumber, string? Status, int? AssignedToId, DateTime? PurchasedAt, DateTime? WarrantyExpiresAt, string? Notes, string? Department, string? Location);
record UpdateAssetRequest(string AssetTag, string Name, string Type, string? SerialNumber, string Status, int? AssignedToId, DateTime? PurchasedAt, DateTime? WarrantyExpiresAt, string? Notes, string? Department, string? Location);
record CreateTicketRequest(string Subject, string Description, string Category, string? Subcategory, string Priority, string? Department, int? RequesterId, int? AssetId);
record UpdateTicketRequest(string Subject, string Description, string Category, string? Subcategory, string Priority, string Status, string? Department, int? AssignedTechnicianId, int? AssetId, string? ResolutionNotes);
record CommentRequest(string Body, bool IsInternal);
record LinkRequest(int RelatedTicketId);
record SlaPolicyItem(string Priority, int ResponseMinutes, int ResolutionMinutes);
record UpdateSlaPoliciesRequest(List<SlaPolicyItem> Policies);
record LookupItemRequest(string Name, bool? IsActive);
record CreateHolidayRequest(string Name, DateTime Date);
record BusinessHourDay(int DayOfWeek, bool IsOpen, string? OpenTime, string? CloseTime);
record UpdateBusinessHoursRequest(List<BusinessHourDay> Days);

class MySqlConnectionFactory(string connectionString)
{
    public async Task<MySqlConnection> OpenAsync()
    {
        var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }
}

// Plain mutable counter bucket used while building the SLA compliance report - one per priority,
// plus one for the "overall" totals row. Not exposed directly; MapSlaReportBucket turns it into JSON.
class SlaReportBucket
{
    public int Total;
    public int ResponseMet;
    public int ResponseBreached;
    public int ResponsePending;
    public int ResponseCurrentlyBreached;
    public int ResolutionMet;
    public int ResolutionBreached;
    public int ResolutionPending;
    public int ResolutionCurrentlyBreached;
}
