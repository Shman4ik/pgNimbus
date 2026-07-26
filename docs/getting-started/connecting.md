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

## Where your password goes

Passwords are handed to the operating system's credential store at connect time:
DPAPI on Windows, a permission-restricted file elsewhere. They are never written
into the connection profile.

That is a design rule rather than a setting. The profile record has no field to
put a password in, so a profile file cannot leak one even if you copy it
somewhere.

## Saved profiles

Save a connection and it appears in the list on the left of the dialog. Each row
shows who connects where, as `user@host/database`, so two profiles on the same
server are told apart without clicking either.

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

## SSH tunnels

A profile can carry SSH tunnel settings, so a database that is only reachable
from a bastion host connects like any other. The tunnel is owned by the window,
so closing the window tears it down.

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
reopened quietly on your next run. pgNimbus flushes the dead pool and retries
once on a fresh connection, so you usually will not notice.

The one case it deliberately does not paper over is an open explicit transaction.
A transaction lives on one held connection; if that connection dies, the
transaction is gone and nothing in it committed. Rather than silently starting a
new one and leaving you to guess what happened, pgNimbus surfaces a clear
"connection lost, nothing committed" error.

!!! tip "Skipping the dialog"

    Set the `PGNIMBUS_CONN` environment variable to any of the formats above and
    pgNimbus connects straight to it, skipping the dialog. Handy for scripted or
    repeated local testing:

    ```bash
    export PGNIMBUS_CONN="postgres://postgres:secret@localhost:5432/mydb"
    ```

    For everyday use there is a switch in Preferences, **Open the last
    connection on startup**, which goes straight to whatever you connected to
    last. The dialog stays one Switch connection away, and a connect that fails
    lands back in it with the error.

## Next

[Get to know the SQL editor](../guide/editor.md)
