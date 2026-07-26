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

Save a connection and it appears in the list on the left of the dialog.
Double-click a profile to connect to it.

Give production a colour. Each profile carries an accent colour, shown as a dot
in the main window's command bar and used through the window's chrome. Making
production red and staging green is the cheapest possible guard against running
the right query against the wrong server.

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

## Next

[Get to know the SQL editor](../guide/editor.md)
