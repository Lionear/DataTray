- **SQL Server Agent jobs can be started, stopped, enabled and disabled**, the way SSMS does it from a job's
  context menu. The four actions live in the SQL Server Admin Dialogs tool and appear under *Tools* on a job
  node — nothing to fill in, so the dialog is a short explanation, a button and the log Agent's answer lands
  in. The job editor (steps, schedules, notifications) is not part of this.
- **The job list says what it is worth saying at a glance.** A job that is switched off reads *disabled*, a
  job whose last run failed, retried or was cancelled carries that as a badge, and hovering any job shows
  when it last ran. A job that has never run stays unlabelled rather than being reported as failed.
