# Backup

pgNimbus backs up with PostgreSQL's own `pg_dump`, so a backup is a normal
PostgreSQL backup that any PostgreSQL tool can read and restore. It covers the
simple cases: all of a database, one schema or one table, with or without the
rows. For scheduled backups, parallel dumps, roles and other cluster-wide
objects, use a dedicated tool such as `pg_dumpall`, pgBackRest or your cloud
provider's snapshots.

![The backup window: where to save, and everything or the structure only](../screenshots/backup.png)

## Back up a database, a schema or a table

- The whole database: **Back Up Database…** in the ☰ menu, the command palette,
  or the **File** menu on macOS.
- One schema: right-click it in the schema tree and choose **Back Up Schema…**.
- One table, with its partitions if it has any: right-click it and choose
  **Back Up Table…**.

The window asks two things:

1. **Where to save.** pgNimbus suggests a file named after the database and the
   time, in the folder you used last. **Change…** picks another.
2. **What to save.** **Everything** is the structure and every row.
   **Structure only** is the tables, views, functions, types and permissions,
   without the rows.

The kind of file follows its name:

- **`.dump`** (the default) is `pg_dump`'s compressed archive. Restore it with
  `pg_restore`.
- **`.sql`** is a plain SQL script you can read, compare and keep in Git.
  Restore it with `psql`.

Click **Back Up**. The window shows which table is being saved, how long it has
taken and how big the file is so far. You can keep working in the main window
while it runs. When it finishes, **Show in Folder** opens the folder that holds
the file.

!!! tip "A failed backup never destroys an older one"

    `pg_dump` writes to a temporary `.partial` file next to the one you chose,
    and pgNimbus moves it into place only when the backup finishes. If the
    backup fails, or you click **Stop**, the temporary file is deleted and a
    file that was already at that path stays as it was.

## Installing pg_dump

pgNimbus doesn't ship `pg_dump` and doesn't download it. It finds the copy that
comes with PostgreSQL, pgAdmin, Postgres.app or Homebrew, in the places they
install it, so most people never have to point it anywhere.

`pg_dump` must be at least as new as the server: a PostgreSQL 17 server needs
`pg_dump` 17 or newer. A newer `pg_dump` backs up every older server, so
installing the newest release is always enough. When no copy is new enough,
the backup window says which version it needs, what it found instead, and the
steps for your system. In short:

=== "Windows"

    Download the PostgreSQL installer from
    [EDB](https://www.enterprisedb.com/downloads/postgres-postgresql-downloads),
    run it, and on the **Select Components** page leave only
    **Command Line Tools** checked. Or, from a terminal:

    ```
    winget install -e --id PostgreSQL.PostgreSQL.18 --interactive
    ```

    and choose **Command Line Tools** when the installer asks. pgNimbus finds
    `C:\Program Files\PostgreSQL\18\bin` on its own. If pgAdmin 4 is
    installed, pgNimbus can use the copy that comes with it too.

=== "macOS"

    With Homebrew:

    ```
    brew install libpq
    ```

    Or install [Postgres.app](https://postgresapp.com/downloads.html); it
    includes the client programs, and you don't have to start its server.

=== "Linux"

    On Debian and Ubuntu, add PostgreSQL's own package repository (your
    distribution's packages are often older than the server), then install the
    client:

    ```
    sudo apt install -y postgresql-common
    sudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh
    sudo apt install postgresql-client-18
    ```

    On Fedora, RHEL and their relatives, set up the
    [PostgreSQL repository](https://www.postgresql.org/download/linux/redhat/)
    and install `postgresql18`. On Arch, install `postgresql-libs`.

Then click **Look Again** in the backup window.

If your copy is somewhere pgNimbus doesn't look, open **Settings**, go to the
**Data** tab and choose the folder that holds `pg_dump` and `pg_restore`.
The same card shows which copy pgNimbus uses.

## Your password and the command line

pgNimbus starts `pg_dump` with the same server, database, user and TLS settings
as the window you started the backup from. It passes the password in
`pg_dump`'s environment and never on its command line, which any user on the
computer can read. Under the form, **Command** shows the exact command pgNimbus
runs, without the password, so you can copy it and run it yourself.

Through an SSH tunnel, `pg_dump` goes through the window's tunnel, so keep the
window open until the backup finishes. pgNimbus asks before closing a window
whose backup is still running.
