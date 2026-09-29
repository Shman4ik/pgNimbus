# Connecting to a database

pgNimbus opens the connection dialog on launch.

![The connection dialog, with saved profiles on the left and the paste-anything import box at the top](../screenshots/connection-dialog.png)

## Paste anything

The box at the top of the dialog accepts whatever form of connection string you
already have, and fills the form out for you. All of these work:

```text
postgres://alice:s3cret@db.example.com:5433/appdb?sslmode=require
jdbc:postgresql://db.example.com:5433/appdb?user=alice&ssl=true
Host=db.example.com;Port=5433;Database=appdb;Username=alice;Password=s3cret
host=db.example.com port=5433 dbname=appdb user=alice sslmode=require
PGPASSWORD=s3cret psql -h db.example.com -p 5433 -U alice appdb
```

That last one means you can copy a `psql` command straight out of a runbook or a
hosting provider's dashboard and paste it in.

A password with `/`, `?`, `#` or `@` in it doesn't have to be percent-encoded
in a `postgres://` URI: everything before the last `@` is read as the user name
and password.

The copy button next to the box copies the form as a `postgres://` URI without
the password, because clipboard history and clipboard managers keep what you
copy. To copy it with the password, right-click the button and choose **Copy
With Password**. pgNimbus then asks Windows clipboard history (and clipboard
managers on macOS and Linux that honor the hint) not to keep it, and clears the
clipboard after 30 seconds if the string is still there.

## Where your password goes

Database and SSH passwords are kept separately from the connection profile:
DPAPI-encrypted files on Windows, Keychain on macOS, and Secret Service through
libsecret on Linux. A password is written there a moment after you stop typing
it. Clearing the field removes the stored one. These stores can persist
protected data on disk.

Linux requires `libsecret-1.so.0` and a running Secret Service provider such as
GNOME Keyring. If storage is unavailable or access is denied, the dialog warns
and keeps entered passwords in memory for this app session. Unlock/configure
the OS store and edit the password again to retry. On macOS, Keychain access must be available
without an interactive authorization prompt; resolve restrictions in Keychain Access.
Builds that are signed ad hoc change identity with every version, so after an update the
Keychain can refuse the passwords an earlier version saved. The
[macOS installation notes](installation.md#macos) have the fix.

On macOS and Linux, older versions kept passwords in unencrypted base64 `.cred`
files. When the connection dialog first opens, pgNimbus moves all of them into
the OS store at once. It deletes an old file only after reading the saved
password back from the OS store. Files it can't move stay where they are, and
the dialog shows one warning with their count. If a different OS-store value
already exists, it takes precedence; editing the password resolves the old copy.
Passwords that the OS store refused are kept in memory only until the last
window using that connection closes.
Deleting a profile attempts to remove database and SSH credentials from both
locations and reports failures.

Credential protection does not cover your queries. Three files keep SQL text on
disk, unencrypted, in `<appdata>/pgNimbus/` (on Windows, `%AppData%\pgNimbus`;
see [where pgNimbus keeps its files](installation.md#where-pgnimbus-keeps-its-files)):

- `history.json` keeps the text of every statement you run, with the values in
  it. A query that looks up a customer by email keeps the email address. Each
  entry also keeps its result line: the row count, or the server's error
  message. A statement that held a password keeps no result line, because an
  error message can quote the password where masking can't find it.
- `workspace.json` keeps the text of your open tabs, so the next session can
  reopen them.
- `saved-queries.json` keeps the queries you save to the Saved Queries list,
  exactly as you saved them.

Before the first two files are written, pgNimbus masks passwords it finds in the SQL:
`PASSWORD '…'` in `CREATE ROLE` and `ALTER ROLE`, including inside a `DO` block,
an `EXECUTE` string or a comment, and `password=…` in a connection string such
as `CREATE SUBSCRIPTION … CONNECTION '…'` or `dblink_connect('…')`. The
password is replaced with `'<redacted>'::redacted`, so a restored tab or a history
entry shows that instead. If you run such a statement again, PostgreSQL rejects
it as a syntax error rather than setting the password to the placeholder. Other
values are kept as you typed them. A tab opened from a `.sql` file that holds a
password is not copied at all: next time it opens from the file itself, so any
changes you had not saved to that file are gone.

Masking needs a word next to the secret that says what it is. A key passed as an
ordinary argument, such as `pgp_sym_encrypt(data, 'key')`, or a password kept in
a variable inside a `DO` block, is stored as you typed it.

To stop recording history, turn off **Record query history** in Settings. Queries
you run after that are not written anywhere, and the history list says that
history is off. Entries already recorded stay until you right-click the list and
choose **Clear History**; pinned entries survive that, so unpin them first.

On macOS and Linux, pgNimbus makes its data folder and every file in it readable
by your user account only, including files an older version left open to other
users.

That is a design rule rather than a setting. The profile record has no field to
put a password in, so a profile file cannot leak one even if you copy it
somewhere.

## Saved profiles

There is no Save button. Start typing into an empty form, or paste a connection
string, and the connection appears in the list on the left of the dialog. Every
later change to a selected profile is kept as you make it, whether you connect
or not. Press **New** for a blank form. Each row shows who connects where, as
`user@host/database`, so two profiles on the same server are told apart without
clicking either.

The connection you used last is already selected when the dialog opens, with the
password loaded, so launching pgNimbus and pressing <kbd>Enter</kbd> reconnects
to it. Pick another with the arrow keys and press <kbd>Enter</kbd>, or
double-click any profile to connect to that one.

Right-click a profile for Connect, Duplicate and Delete. Duplicate is the fast
way to add a second database on the same server: it copies the host, SSL mode,
SSH settings and password, and you change only what differs.

Give production a colour. Each profile carries an accent colour, picked from the
round swatch next to the name field. It shows as a dot in the main window's
command bar and runs through the window's chrome. Making production red and
staging green is the cheapest possible guard against running the right query
against the wrong server.

## Read-only connections

Switch on **Read-only session** for a profile you only mean to read from, such
as production. Every session then starts with `default_transaction_read_only`
on, so the server itself refuses `INSERT`, `UPDATE`, `DELETE`, `COPY FROM` and
DDL. It refuses them from the editor, the results grid, an import and a schema
action alike, because the check isn't in pgNimbus. The profile gets a lock in
the list.

The main window shows **read-only** with a lock next to the host and database,
and the results grid doesn't offer editing. pgNimbus asks the server when the
window opens, so the mark also appears when the server makes the session
read-only on its own: a role or database with `default_transaction_read_only`
set, or a standby replica.

A connection pooler can drop the read-only option before it reaches the server.
PgBouncer does this when `ignore_startup_parameters` includes `options`. If the
profile asks for read-only and the server still accepts writes, the mark turns
amber and reads **read-only not applied**. The results grid stays read-only,
but SQL you run can change data, so don't count on the profile's protection
for that session.

This guards against mistakes. It isn't a permission. A statement can still
switch it off for its own session with `SET default_transaction_read_only =
off`. To make writes impossible, connect as a role that has no write
privileges.

## Encryption (TLS)

**SSL Mode** decides whether the connection is encrypted and what pgNimbus
checks about the server. The list describes each mode. In short:

| Mode | Encrypted | Checks the certificate | Checks the host name |
|---|---|---|---|
| Disable | No | No | No |
| Allow | Only if the server insists | No | No |
| Prefer | If the server offers it | No | No |
| Require | Yes | No | No |
| Verify CA | Yes | Yes | No |
| Verify full | Yes | Yes | Yes |

New profiles start at **Require** for a server on the network. It always encrypts, but it does not check
who is on the other end: anyone who can intercept the connection can present
their own certificate and read everything, the password included. Prefer and
Allow are weaker still, because an attacker can make them fall back to
plaintext. Use **Verify full** for any server you reach over a network you do
not control. It is marked as recommended in the list.

A server on this machine (`localhost`, `127.0.0.1`, `::1`) starts at **Prefer**
instead: there is no network path to intercept, and a local Postgres, such as
the `postgres` Docker image, has no TLS unless you set it up. The mode follows
the host you type until you pick one yourself. If Require meets a server
without TLS, the connection fails with a message saying so.

### Root certificate

Verify CA and Verify full trust the certificate authorities your operating
system trusts. Managed Postgres services often sign their servers with their
own CA, which no operating system trusts, so verification fails until you point
pgNimbus at that CA. Choosing Verify CA or Verify full shows a **Root
Certificate** field. Give it the provider's CA file, with **Browse…** or by
typing the path:

- **Amazon RDS and Aurora:** the global bundle, `global-bundle.pem`, from
  `https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem`.
- **Google Cloud SQL:** the server CA certificate (`server-ca.pem`) from the
  instance's Connections page, under Security. If the instance's certificate
  does not name the host you connect to, use Verify CA.
- **Supabase:** the certificate from the project's database settings, under
  SSL Configuration.

Leave the field empty to use the operating system's store. The path is saved
with the profile. It is not a secret. A pasted connection string fills it from
`sslrootcert=` (libpq, URIs, JDBC) or `Root Certificate=` (Npgsql).

### Through an SSH tunnel

Verify full works through an SSH tunnel. The tunnel connects pgNimbus to a
local port, but the certificate is still checked against the database host you
typed in the form, which is the name the server's certificate carries.

## SSH tunnels

A profile can carry SSH tunnel settings, so a database that is only reachable
from a bastion host connects like any other. The tunnel is owned by the window,
so closing the window tears it down.

Pick how pgNimbus signs in to the SSH host with **Auth Method**:

- **SSH agent** uses the keys your agent already holds. On Windows that is the
  OpenSSH Authentication Agent service; on macOS and Linux it is the agent
  `SSH_AUTH_SOCK` points at. A key with a passphrase works without typing it
  here, as long as you have added it with `ssh-add`. Nothing is stored.
- **Key file** reads a private key from disk. Leave the path blank to use the
  same defaults `ssh` tries (`~/.ssh/id_ed25519`, then `id_ecdsa`, then
  `id_rsa`). If the key has a passphrase, type it below; it is stored like a
  password.
- **Password** signs in with the SSH user's password.

For the database host and port, give them as the SSH host sees them. A Postgres
that only listens locally on the server is `127.0.0.1` and `5432`.

### Host keys

Before it signs in, pgNimbus checks that the SSH host is the server it claims
to be, the same way `ssh` does. It looks the host's key up in your own
`~/.ssh/known_hosts` first (hashed entries, `[host]:port` entries and
`@revoked` lines included) and then in its own list at
`<appdata>/pgNimbus/known_hosts`. It never writes to your `~/.ssh/known_hosts`.

- **A known key** connects with no question.
- **An unknown host** opens a dialog with the host, the key type and the key's
  `SHA256:` fingerprint, written the way `ssh` and `ssh-keygen -l` write it.
  Compare it with the fingerprint the server's administrator gave you.
  **Accept** connects and adds the key to pgNimbus's list; **Cancel** does not
  connect. Enter does not accept, so a key is never trusted by a stray key
  press.
- **A changed key** stops the connection. The message names the host, the
  stored key's fingerprint, the new one, and the file and line that hold the
  old key. A server that was reinstalled or had its keys regenerated causes
  this, and so does someone intercepting the connection. If you know the key
  really changed, remove that line (`ssh-keygen -R` with `-f` pointing at that
  file does it for you) and connect again to be asked about the new key.
- **A key of a different type** for a host already in either file stops the
  connection too. pgNimbus asks the server only for the key types the files
  know the host by, as `ssh` does, so a server that suddenly offers only a new
  type of key is refused rather than treated as a new host. If its keys really
  changed, remove its lines and connect again.
- **A revoked key** (an `@revoked` line in either file) always stops the
  connection.

Take your time with the dialog: if reading the fingerprint outlasts the
connection's timeout, pgNimbus connects again once you accept, without asking
a second time.

The **Test** button runs the same check, so a key you accept there is already
known when you connect. SSH host certificates (`@cert-authority` lines) are not
supported yet: a host that relies on one is treated as unknown and asks about
its key.

## Several connections at once

Two different things, for two different needs.

Open a connection in a new window (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>N</kbd>)
gives that connection a fully independent window: its own connection pool,
`LISTEN`/`NOTIFY` listener, SSH tunnel, and workspace of tabs. Use this to put dev
and prod side by side.

Switch connection (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>O</kbd>) repoints the
current window at a different server without restarting the app.

Both are also in the menu behind the ☰ button and in the command palette.

## If the connection drops

A connection dropped by laptop sleep, a network blip or an SSH tunnel hiccup is
reopened quietly on your next run. Before it sends a statement, pgNimbus asks
the server to describe it, which runs nothing. If that fails because the
connection is dead, pgNimbus flushes the pool and opens a fresh connection, so
you usually will not notice.

pgNimbus never sends a statement a second time on its own. If the connection
drops while a statement is running, whether the network went away or a DBA
terminated your session, the run ends with an error that says the statement
was not run again and may or may not have taken effect. Check before you run
it again. The next statement reconnects by itself. A statement that has
already started on the server is out of the client's hands: an `UPDATE` that
was halfway through when the socket died keeps running there and commits,
and running it again would apply it twice.

The other case pgNimbus deliberately does not paper over is an open explicit
transaction. A transaction lives on one held connection; if that connection
dies, the transaction is gone and nothing in it committed. Rather than silently
starting a new one and leaving you to guess what happened, pgNimbus surfaces a
clear "connection lost, nothing committed" error.

!!! tip "Skipping the dialog"

    Set the `PGNIMBUS_CONN` environment variable to any of the formats above and
    pgNimbus connects straight to it, skipping the dialog. Handy for scripted or
    repeated local testing:

    ```bash
    export PGNIMBUS_CONN="postgres://postgres:secret@localhost:5432/mydb"
    ```

    The example above puts the password in the environment. Any other process
    running as you can read it (`/proc/<pid>/environ` on Linux, a process
    inspector on Windows or macOS), and if you type the `export` line directly
    at a shell, it usually lands in shell history too. Prefer a connection
    string with no password and let pgNimbus prompt, or keep this variable to
    a throwaway local database.

    For everyday use there is a switch in Settings, **Open the last
    connection on startup**, which goes straight to whatever you connected to
    last. The dialog stays one Switch connection away, and a connect that fails
    lands back in it with the error.

## Next

[Get to know the SQL editor](../guide/editor.md)
