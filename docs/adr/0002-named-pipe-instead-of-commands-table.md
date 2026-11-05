# Tray reaches the Collector over a named pipe, and the DB is read-only for users

The original plan used a `commands` table in the shared SQLite DB as the only UI-to-Collector channel, which meant granting `BUILTIN\Users` write access to the DB folder. Any user could then rewrite or delete the recorded history. diskdrift is personal for now but is meant to become a distributable tool, so we lock the data folder to SYSTEM and Administrators for writing, give Users read-only access, and carry Tray requests ("probe now", "are you alive") over a named pipe whose ACL admits interactive users.

## Considered Options

- **`commands` table with Users write access:** simplest, but it hands every user write access to the data.
- **Separate world-writable command DB:** keeps the history safe, but it is still polling and adds a second file to manage.
- **Named pipe (chosen):** small amount of code, immediate response, and a working pipe doubles as the "Collector running" check.
