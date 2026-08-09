- **The Activity Monitor stops rebuilding its grid on every refresh.** Each auto-refresh threw the whole
  column layout away and built it back identically, so column widths you had dragged reset every five
  seconds, and a refresh landing at the wrong moment could take the app down with it — a crash reported
  as "it dies when I click the Activity Monitor again" that nobody could reproduce. The refresh now
  replaces only the rows unless the columns genuinely changed. A monitor tab you are not looking at no
  longer queries the server at all, and comes back with fresh sessions the moment you return to it.
- **A row action can no longer act on a session that has just been refreshed away.** With the context
  menu open across an auto-refresh, *Kill* and *Cancel Query* still pointed at the row from the previous
  snapshot.
- **A crash now leaves something behind.** An unhandled error took the app down without writing anything,
  so a crash you could not reproduce was a dead end for whoever had to look into it. It is now appended
  to `restart.log` in the app's settings folder, stack trace and all.
