# DeskFlow

A C# (.NET 10) API + single-page frontend for an IT help desk / asset tracker, built one slice
at a time: **Login/Logout → Users → Assets → Tickets**.

## What's in here

```
deskflow/
├── Dockerfile                       <- packages the app for Render/Railway/any Docker host (see below)
├── .dockerignore
├── users.sql                        <- run first in MySQL Workbench
├── assets.sql                       <- run second in MySQL Workbench
├── tickets.sql                      <- run third in MySQL Workbench
├── sla_policies.sql                 <- run fourth - creates the sla_policies table + default rules
├── sla_migration.sql                <- run fifth - upgrades tickets to the two-clock SLA engine
├── settings_tables.sql              <- run sixth - creates every Settings table (see below)
├── settings_migration.sql           <- run seventh - adds department/location columns to assets
└── DeskFlow.Api/
    ├── DeskFlow.Api.csproj
    ├── Program.cs                   <- the whole backend (auth, users, assets, tickets, SLA, settings)
    ├── appsettings.json             <- your MySQL connection string
    └── wwwroot/
        ├── login.html               <- sign-in page
        └── app.html                 <- the app shell (sidebar + Users, Assets, Tickets, Dashboard,
                                         Technicians, Reports, Settings)
```

## 1. Create the database tables

You already created the `deskflow` database in Workbench. Run these in order (make sure
`deskflow` is the active schema each time): `users.sql`, `assets.sql`, `tickets.sql`,
`sla_policies.sql`, `sla_migration.sql`, `settings_tables.sql`, `settings_migration.sql`.
`assets.sql` has a foreign key back to `users`, `tickets.sql` has foreign keys back to both
`users` and `assets`, `sla_migration.sql` alters the `tickets` table itself, and
`settings_migration.sql` alters the `assets` table, so the order matters. If you already had
`deskflow` set up before this Settings update, you only need to run the two new files
(`settings_tables.sql` then `settings_migration.sql`) — both are safe to run against a database
that already has data in it. None of these files insert ticket/user/asset seed data — the API
creates a default admin account itself the first time it runs (see step 4).

## 2. Set your MySQL password

Open `DeskFlow.Api/appsettings.json` and set your actual MySQL root password (the one you use
to log into Workbench). Port is set to **7164** to match your MySQL server instead of the
default 3306:

```json
"DeskFlow": "Server=localhost;Port=7164;Database=deskflow;User=root;Password=YOUR_PASSWORD_HERE;"
```

## 3. Install the .NET 10 SDK (if you don't have it)

Check first: `dotnet --version`. If that fails, grab the SDK from
[dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) (get the **SDK**, not
just the runtime).

## 4. Run it

```bash
cd DeskFlow.Api
dotnet restore
dotnet run
```

The first run creates a default admin account automatically:
**username:** `admin` **password:** `Admin@123`

Change that password once you're in — don't leave the default active.

## 5. Open it

Go to **http://localhost:5000**, sign in with the account above, and you'll land in the app
shell — sidebar on the left, with Dashboard, Users, Assets, Tickets, Technicians, Reports, and
Settings all fully working. Notifications is the one remaining item still on the roadmap (see
"Where to go next").

## How the pieces fit together

- `users` table stores `password_hash` (bcrypt), never a plain-text password.
- `POST /api/auth/login` checks credentials and issues an HTTP-only session cookie
  (`deskflow_auth`) via ASP.NET Core's built-in cookie authentication — no JWT/localStorage
  handling on the frontend.
- Two authorization policies gate everything past login: **AdminOnly** (creating/editing/
  deleting user accounts) and **AgentOrAdmin** (browsing the user directory, managing assets —
  day-to-day IT work that shouldn't require full admin rights). Every protected route checks
  this server-side, on every request — the sidebar just hides links a role can't use, it isn't
  what's actually stopping anyone.
- `assets` table has a foreign key to `users` (`assigned_to_id`) with `ON DELETE SET NULL`, so
  deleting a user never breaks an asset record — it just un-assigns it.
- `tickets` table has foreign keys to `users` (requester, assigned technician) and `assets`
  (related asset), all `ON DELETE SET NULL` for the same reason — a ticket is a record, and
  deleting the person or device it refers to shouldn't delete the ticket's history.
- `app.html` is one file with a sidebar, a top bar, and a set of "views" that show/hide by
  JavaScript — no page reloads when you click between Users, Assets, and Tickets. `login.html`
  redirects here after a successful sign-in.

## How Tickets works

- **Lifecycle**: `New → Assigned → In Progress → On Hold → Resolved → Closed`. Only agents and
  admins can change status or assignment — requesters file a ticket and can add public comments,
  but can't edit it or reassign it themselves.
- **Ticket numbers** (`TK-1024` style) are generated server-side right after the row is inserted,
  from the auto-increment id — never user-supplied.
- **SLA tracking**: every ticket runs two independent SLA clocks (first response, and resolution),
  driven by admin-configurable rules and able to pause while a ticket is On Hold — see
  "How the SLA engine works" below for the full picture.
- **Comments vs. internal notes**: only agents/admins can mark a comment "internal" — those are
  filtered out server-side before a requester ever sees the response, not just hidden in the UI.
- **History/audit trail**: every field change (status, priority, category, assignment, related
  asset) writes a row to `ticket_history` with who changed it and the old/new value. There's no
  "undo" — it's an append-only log, same spirit as the ticket having no DELETE endpoint at all.
- **Requester visibility**: `GET /api/tickets` and `GET /api/tickets/{id}` both filter server-side
  — a requester only ever gets their own tickets back, whether they ask nicely or not.
- **Related tickets**: agents/admins can link two tickets together (stored both directions, so it
  shows up from either ticket's detail view) and unlink them later.
- **Reopening**: a requester can reopen their own resolved/closed ticket; agents/admins can reopen
  any. Reopening sets status back to `in_progress` and clears the resolved/closed timestamps and
  the resolution clock's outcome — first response stays on the record, since it already happened.
- Asset linking on ticket creation is agent/admin-only — requesters don't get an asset picker on
  the "New Ticket" form (they don't have Assets access), so linking the right device happens
  during triage instead.

## How the SLA engine works

Every ticket runs **two independent clocks**, each with its own admin-configurable deadline:

| Priority | Response time | Resolution time |
|---|---|---|
| Critical | 15 min | 2 hours |
| High | 30 min | 4 hours |
| Medium | 2 hours | 8 hours |
| Low | 4 hours | 24 hours |

These live in the `sla_policies` table (one row per priority) and are editable from
**Settings → SLA Rules** by admins — agents can see them there too, read-only. Nothing about the
SLA engine is hardcoded in `Program.cs` anymore; it looks the current rules up from the database
on every request.

- **Starting the clocks**: both `response_due_at` and `resolution_due_at` are set the moment a
  ticket is filed (`POST /api/tickets`), based on the policy for its priority at that time.
- **First response**: whichever happens first — a technician/admin triaging the ticket out of
  `New`, or posting the ticket's first comment — sets `first_responded_at` and locks in
  `response_met` (true/false, compared against `response_due_at`). This is a one-time fact and is
  never reset, even if the ticket is later reopened.
- **Resolution**: moving a ticket into `Resolved` or `Closed` for the first time sets
  `resolved_at` and locks in `resolution_met`. Going straight from `Resolved` to `Closed` doesn't
  touch `resolved_at` again — both are treated as "the ticket is done," so the original resolution
  timestamp is preserved instead of being overwritten or cleared.
- **Pausing on Hold**: moving a ticket to `On Hold` records `on_hold_since` and freezes the
  countdown right there — the remaining-time display stops advancing. Moving it off `On Hold`
  banks the elapsed pause into `total_paused_minutes` and pushes **both** `response_due_at` and
  `resolution_due_at` forward by that amount, so a ticket paused for two hours doesn't lose two
  hours of SLA the moment work resumes.
- **Remaining time / at risk / breached**: `GET /api/tickets` and `GET /api/tickets/{id}` compute
  live fields on every response — `slaPhase` (`response`, `resolution`, or `done`), `slaDueAt`,
  `slaRemainingSeconds`, `isSlaOnHold`, `isSlaBreached`, and `isSlaAtRisk`. "At risk" is a
  **percentage** of the phase's total allotted time (≤20% remaining) rather than a flat cutoff —
  a flat "2 hours left" warning would be meaningless for a 15-minute Critical response SLA and
  would fire almost immediately for a 24-hour Low resolution SLA.
- **Live countdown**: the ticket list and ticket detail view show the active clock as
  `🟢 Response SLA: 00:18:42 remaining`, ticking down once a second in the browser, switching to
  `🟠` inside the at-risk window, `🔴` once breached, and `⏸️` (frozen) while On Hold. Once a
  ticket is resolved/closed it shows a final `🟢 SLA met` / `🔴 SLA missed` verdict instead of a
  countdown.
- **Compliance reporting**: `GET /api/sla/report` (Reports → SLA Compliance) rolls up every
  ticket's `response_met`/`resolution_met` outcomes into met/breached/pending counts and a
  compliance percentage, both overall and broken out by priority — plus a live "currently over"
  count for tickets whose active clock has already run past its due date but hasn't been marked
  breached yet (because the ticket's still open).

## How Settings works

Settings is a left-nav page with 13 sections. What's real varies by section — this list is meant
to be an honest map, not a sales pitch:

- **Fully live, changes take effect immediately**: Company Information, Departments, Locations,
  Ticket Categories, Asset Categories, SLA Policies, Numbering Format, Appearance. Departments and
  Ticket Categories feed the New Ticket form; Departments, Locations, and Asset Categories feed the
  asset form; Numbering Format controls the prefix/starting offset for new ticket numbers
  (`TK-1001` by default) going forward — existing ticket numbers never change. Appearance persists
  a font color and a background color and applies them at load via two CSS custom properties
  (`--text-dark`, `--panel-bg`) that the whole signed-in app already reads from — it only affects
  the signed-in app shell, not `login.html`.
- **Departments/Locations/Categories are plain pick-lists, not foreign keys** — tickets and assets
  store the chosen name as text, so deactivating or deleting an entry never touches an existing
  ticket or asset record; it just stops showing up as a dropdown option going forward. Assets
  gained `department`/`location` columns as part of this (`settings_migration.sql`), which is what
  makes the new "Assets by Department" / "Assets by Location" reports on the Reports page real.
- **Stored, but not yet wired into real behavior**: Business Hours and Holidays save correctly, but
  SLA due-date math still runs on wall-clock minutes around the clock — it doesn't yet pause
  outside business hours or skip holidays. Notifications and Email Configuration also save
  correctly (the stored SMTP password is never sent back to the browser — leave the password field
  blank on save to keep the current one), but nothing actually sends yet; that's the same
  Notifications feature already on the roadmap below.
- **Deliberately read-only**: Ticket Priorities, Ticket Statuses, and User Roles are shown under
  "System Definitions" for reference, not as editable settings. All three are load-bearing ENUM
  columns wired directly into the SLA engine (pause/resume logic, breach detection) and the
  authorization policies (`AdminOnly`/`AgentOrAdmin`) — making them freely editable would need real
  engineering work on those systems, not a settings toggle, so they stay fixed on purpose.
- **Access**: every Settings `GET` is agent-or-admin (agents can see the same lists they use
  day-to-day); every add/edit/delete/save is admin-only, enforced server-side the same way as
  everywhere else in the app — the UI just hides the controls an agent can't use.

## How Google Sign-In works

Google sign-in is **optional** and costs nothing — it uses a free, standard Google OAuth 2.0
Client ID, not any paid Google identity product. Left unconfigured, the app works exactly as
before: username/password only, no Google button shown anywhere.

- **It's additive, not a replacement.** Username/password login keeps working exactly as it does
  today. "Sign in with Google" is a second option on `login.html`, shown only once Google
  credentials are configured.
- **No self-signup.** Google sign-in only works for an email address that's already a DeskFlow
  user — created the normal way, by an admin, under Users. If someone signs in with a Google
  account whose email doesn't match an existing DeskFlow user, they're sent back to the login page
  with a "no account" message and told to contact an admin. There's no path from a Google sign-in
  to a brand-new account being created.
- **How matching works:** the callback reads the verified email address Google returns, looks it
  up in the existing `users` table (same table/column username/password login already checks), and
  if it finds an active user, signs them in with the exact same session cookie and role claims
  username/password login would produce. Every existing admin/agent permission check keeps working
  unchanged, no matter which way someone signed in. No new database table or column was needed for
  this.

### Setting it up

1. In [Google Cloud Console](https://console.cloud.google.com/), create a project (or use an
   existing one) and open **APIs & Services → OAuth consent screen**. Fill in the basics (app
   name, support email) — for internal/company use, "Internal" user type keeps it restricted to
   your own Google Workspace domain if you have one.
2. Go to **APIs & Services → Credentials → Create Credentials → OAuth client ID**, choose
   **Web application**, and add this **Authorized redirect URI**:
   ```
   http://localhost:5000/signin-google
   ```
   (Adjust the host/port if you run DeskFlow somewhere other than `localhost:5000` — the path
   `/signin-google` itself doesn't change, it's the Google auth library's default callback path.)
3. Copy the **Client ID** and **Client Secret** it gives you into
   `DeskFlow.Api/appsettings.json`:
   ```json
   "Authentication": {
     "Google": {
       "ClientId": "your-client-id.apps.googleusercontent.com",
       "ClientSecret": "your-client-secret"
     }
   }
   ```
4. Restart the app (`dotnet run`). The "Sign in with Google" button appears on the login page
   automatically once both fields are filled in — no code changes needed.
5. Make sure anyone who'll sign in with Google already has a DeskFlow user account (Users → Add
   User) using the **same email address** as their Google account.

Leaving `ClientId`/`ClientSecret` blank at any point fully disables the feature again — the button
disappears and the `/api/auth/google/*` routes stop existing, with no restart-breaking side effects
either way.

## Deploying to the web, for free (Render + Aiven)

This puts DeskFlow on a real `https://...` address anyone can reach, at no cost. Two free
services split the job: **Aiven** hosts the MySQL database, **Render** runs the app itself,
building it from the `Dockerfile` in this folder. The one tradeoff of the free tier: if nobody's
used the app in the last 15 minutes, the next visit takes 30-60 seconds to "wake up" — after
that it's fast again. That's a fine tradeoff for a low-traffic internal tool; if it ever becomes
a problem, upgrading Render to a paid plan removes it, with no code changes needed.

### 1. Put the code on GitHub

Create a new repository on [github.com](https://github.com) and upload this whole `deskflow`
folder to it (GitHub's "upload files" button in the browser works fine for this — no command
line needed). This repo is what Render will build from.

### 2. Create the free MySQL database (Aiven)

1. Sign up at [aiven.io](https://aiven.io/free-tier) (no credit card required for the free tier).
2. Create a new service → **MySQL** → pick the **free plan**.
3. Once it's running, open its "Overview"/"Connection details" page — copy the **host, port,
   user, password, and database name** it gives you.
4. Connect MySQL Workbench to that host/port instead of `localhost`, and run the same SQL files
   in the same order as step 1 above: `users.sql`, `assets.sql`, `tickets.sql`,
   `sla_policies.sql`, `sla_migration.sql`, `settings_tables.sql`, `settings_migration.sql`.
5. Build the connection string DeskFlow needs, same shape as the one in `appsettings.json`, but
   pointed at Aiven and with `SslMode=Required` added (Aiven requires an encrypted connection):
   ```
   Server=YOUR-AIVEN-HOST;Port=YOUR-AIVEN-PORT;Database=defaultdb;User=YOUR-USER;Password=YOUR-PASSWORD;SslMode=Required;
   ```
   Keep this handy — it goes into Render as an environment variable next, not into a file.

### 3. Deploy the app (Render)

1. Sign up at [render.com](https://render.com) and choose **New → Web Service**.
2. Connect the GitHub repo from step 1. Render will detect the `Dockerfile` automatically and
   offer the **Free** instance type.
3. Before the first deploy, add one environment variable under the service's "Environment" tab:
   | Key | Value |
   |---|---|
   | `ConnectionStrings__DeskFlow` | the Aiven connection string from step 2.5 |

   (The double underscore is deliberate — that's how ASP.NET Core maps an environment variable
   name to the nested `ConnectionStrings.DeskFlow` setting from `appsettings.json`. This
   overrides the placeholder value in the file, so nothing in the code or the file itself needs
   to change.)
4. Deploy. Render builds the Docker image and starts the app — first build typically takes a
   few minutes. Once it's live, Render gives you a URL like `https://deskflow-xxxx.onrender.com`.
5. Open that URL and sign in with the default admin account (`admin` / `Admin@123`), then change
   that password right away — it's now reachable on the public internet.

### 4. If you're using Google Sign-In

Two small updates once the app has a real public URL:

- In Google Cloud Console (Credentials → your OAuth client), replace the `localhost` redirect URI
  with `https://YOUR-RENDER-URL/signin-google`.
- In Render's Environment tab, add `Authentication__Google__ClientId` and
  `Authentication__Google__ClientSecret` (same double-underscore pattern as the connection
  string) with your real values — this turns the feature on for the live site the same way
  editing `appsettings.json` turns it on locally.

## Where to go next

- **Notifications** — in-system delivery (a bell/feed wired to ticket lifecycle events like
  assignment, new comments, and SLA breaches) is buildable now. Actually sending email/SMS/Teams/
  Slack needs real credentials from you when we get there — Settings → Email Configuration already
  has a place to store them.
- **Business Hours / Holidays applied to SLA math** — right now they're stored but SLA due dates
  don't pause outside business hours or skip holidays yet; wiring that in changes the due-date
  calculation in `Program.cs`, so it's its own slice of work.
- **Attachments/screenshots** on tickets — deliberately deferred, since file uploads need their
  own design work (storage location, size limits, allowed file types).
- **Asset Depreciation** report — needs a purchase-cost field on `assets` that doesn't exist yet.

Say the word whenever you're ready for the next slice.
