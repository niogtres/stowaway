# Notifications

In **Settings → Notifications**, add a channel to hear about failed backups (and when they work again):

- **Email** (any SMTP server),
- **Webhook** (generic JSON, or Slack-compatible for Slack, Discord and Mattermost),
- **ntfy** push notifications to your phone.

Use **Send test** to check a channel. Warnings and errors are also written to the Windows **Application** event log under the source `Stowaway`.

**See also:** [Watching backups run](06-activity.md), [Remote access](12-remote-access.md)
