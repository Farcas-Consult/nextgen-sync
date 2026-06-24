using System.Net;
using System.Text;

namespace Fcl.Sync.Service.Dashboard;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapSyncDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard", async (ISyncDashboardStore store, CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetDashboardSnapshotAsync(cancellationToken);
            return Results.Content(RenderHtml(snapshot), "text/html; charset=utf-8");
        });

        app.MapGet("/api/dashboard", async (ISyncDashboardStore store, CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetDashboardSnapshotAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        return app;
    }

    private static string RenderHtml(SyncDashboardSnapshot snapshot)
    {
        var latest = snapshot.LatestSyncRun;
        var latestFailedCount = CountStatus(snapshot.LatestCommandCounts, "Failed");
        var healthText = latestFailedCount == 0
            ? "No failed access commands"
            : $"{latestFailedCount:N0} failed access commands";

        var html = new StringBuilder();
        html.AppendLine("""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="refresh" content="30">
              <title>FCL Sync Dashboard</title>
              <style>
                :root {
                  color-scheme: light;
                  --bg: #f6f7f9;
                  --panel: #ffffff;
                  --ink: #17202a;
                  --muted: #637083;
                  --line: #d9dee7;
                  --accent: #116b68;
                  --warn: #9a3412;
                  --bad: #b42318;
                  --ok: #166534;
                }

                * { box-sizing: border-box; }

                body {
                  margin: 0;
                  background: var(--bg);
                  color: var(--ink);
                  font-family: "Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif;
                  font-size: 15px;
                  line-height: 1.45;
                }

                header {
                  background: #ffffff;
                  border-bottom: 1px solid var(--line);
                }

                main, .header-inner {
                  width: min(1180px, calc(100% - 32px));
                  margin: 0 auto;
                }

                .header-inner {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  gap: 16px;
                  padding: 20px 0;
                }

                h1 {
                  margin: 0;
                  font-size: 24px;
                  font-weight: 700;
                  letter-spacing: 0;
                }

                .subtle { color: var(--muted); }
                .mono { font-family: "Cascadia Mono", "Consolas", monospace; }

                main {
                  display: grid;
                  gap: 18px;
                  padding: 18px 0 28px;
                }

                .status-row {
                  display: grid;
                  grid-template-columns: repeat(4, minmax(0, 1fr));
                  gap: 12px;
                }

                .metric, section {
                  background: var(--panel);
                  border: 1px solid var(--line);
                  border-radius: 8px;
                }

                .metric {
                  padding: 14px 16px;
                  min-width: 0;
                }

                .label {
                  color: var(--muted);
                  font-size: 12px;
                  font-weight: 700;
                  text-transform: uppercase;
                }

                .value {
                  margin-top: 5px;
                  font-size: 24px;
                  font-weight: 700;
                  overflow-wrap: anywhere;
                }

                .value.small {
                  font-size: 17px;
                  font-weight: 650;
                }

                section { overflow: hidden; }
                section h2 {
                  margin: 0;
                  padding: 14px 16px;
                  border-bottom: 1px solid var(--line);
                  font-size: 16px;
                  letter-spacing: 0;
                }

                .section-body { padding: 14px 16px; }

                table {
                  width: 100%;
                  border-collapse: collapse;
                }

                th, td {
                  padding: 10px 8px;
                  border-bottom: 1px solid var(--line);
                  text-align: left;
                  vertical-align: top;
                }

                th {
                  color: var(--muted);
                  font-size: 12px;
                  text-transform: uppercase;
                }

                tr:last-child td { border-bottom: 0; }
                .ok { color: var(--ok); }
                .warn { color: var(--warn); }
                .bad { color: var(--bad); }

                .grid-two {
                  display: grid;
                  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
                  gap: 18px;
                }

                @media (max-width: 860px) {
                  .header-inner { align-items: flex-start; flex-direction: column; }
                  .status-row, .grid-two { grid-template-columns: 1fr; }
                  main, .header-inner { width: min(100% - 24px, 1180px); }
                  th, td { padding: 9px 6px; }
                }
              </style>
            </head>
            <body>
            """);

        html.AppendLine("<header><div class=\"header-inner\">");
        html.AppendLine("<div><h1>FCL Sync Dashboard</h1><div class=\"subtle\">GymMaster to local SQLite to access provider</div></div>");
        html.Append("<div class=\"subtle mono\">Updated ");
        html.Append(Escape(FormatTimestamp(snapshot.GeneratedAt)));
        html.AppendLine("</div></div></header>");

        html.AppendLine("<main>");
        html.AppendLine("<div class=\"status-row\">");
        AppendMetric(html, "Members in SQLite", snapshot.MemberCount.ToString("N0"));
        AppendMetric(html, "Latest Sync", latest is null ? "No sync yet" : FormatRunTimestamp(latest), "small mono");
        AppendMetric(html, "Latest Access Health", healthText, latestFailedCount == 0 ? "small ok" : "small bad");
        AppendMetric(html, "Pending Commands", snapshot.PendingCommandCount.ToString("N0"), snapshot.PendingCommandCount == 0 ? null : "warn");
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"grid-two\">");
        html.AppendLine("<section><h2>Latest Run</h2><div class=\"section-body\">");
        if (latest is null)
        {
            html.AppendLine("<div class=\"subtle\">No sync run has completed yet.</div>");
        }
        else
        {
            html.AppendLine("<table><tbody>");
            AppendKeyValueRow(html, "Run ID", latest.Id.ToString());
            AppendKeyValueRow(html, "Mode", latest.Mode);
            AppendKeyValueRow(html, "Status", latest.Status);
            AppendKeyValueRow(html, "Started", FormatTimestamp(latest.StartedAt));
            AppendKeyValueRow(html, "Completed", IsRunning(latest) ? "Running" : FormatTimestamp(latest.CompletedAt!.Value));
            AppendKeyValueRow(html, "Duration", FormatDuration(latest.Duration));
            AppendKeyValueRow(html, "Members Checked", latest.MembersChecked.ToString("N0"));
            if (!string.IsNullOrWhiteSpace(latest.ErrorMessage))
            {
                AppendKeyValueRow(html, "Error", latest.ErrorMessage);
            }

            html.AppendLine("</tbody></table>");
        }

        html.AppendLine("</div></section>");
        html.AppendLine("<section><h2>Latest Command Outcomes</h2><div class=\"section-body\">");
        AppendStatusTable(html, snapshot.LatestCommandCounts);
        html.AppendLine("</div></section>");
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"grid-two\">");
        html.AppendLine("<section><h2>Recent Sync Runs</h2><div class=\"section-body\">");
        AppendSyncRunTable(html, snapshot.RecentSyncRuns);
        html.AppendLine("</div></section>");
        html.AppendLine("<section><h2>All Command Outcomes</h2><div class=\"section-body\">");
        AppendStatusTable(html, snapshot.AllCommandCounts);
        html.AppendLine("</div></section>");
        html.AppendLine("</div>");

        html.AppendLine("<section><h2>Recent Integration Errors</h2><div class=\"section-body\">");
        AppendErrorTable(html, snapshot.RecentErrors);
        html.AppendLine("</div></section>");

        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private static void AppendMetric(StringBuilder html, string label, string value, string? valueClass = null)
    {
        html.AppendLine("<div class=\"metric\">");
        html.Append("<div class=\"label\">").Append(Escape(label)).AppendLine("</div>");
        html.Append("<div class=\"value");
        if (!string.IsNullOrWhiteSpace(valueClass))
        {
            html.Append(' ').Append(Escape(valueClass));
        }

        html.Append("\">").Append(Escape(value)).AppendLine("</div></div>");
    }

    private static void AppendKeyValueRow(StringBuilder html, string key, string value)
    {
        html.Append("<tr><th>").Append(Escape(key)).Append("</th><td class=\"mono\">").Append(Escape(value)).AppendLine("</td></tr>");
    }

    private static void AppendStatusTable(StringBuilder html, IReadOnlyList<CommandStatusCount> counts)
    {
        if (counts.Count == 0)
        {
            html.AppendLine("<div class=\"subtle\">No access commands recorded for this window.</div>");
            return;
        }

        html.AppendLine("<table><thead><tr><th>Status</th><th>Count</th></tr></thead><tbody>");
        foreach (var count in counts)
        {
            html.Append("<tr><td>").Append(Escape(count.Status)).Append("</td><td class=\"mono\">").Append(Escape(count.Count.ToString("N0"))).AppendLine("</td></tr>");
        }

        html.AppendLine("</tbody></table>");
    }

    private static void AppendSyncRunTable(StringBuilder html, IReadOnlyList<SyncRunSummary> runs)
    {
        if (runs.Count == 0)
        {
            html.AppendLine("<div class=\"subtle\">No sync runs recorded yet.</div>");
            return;
        }

        html.AppendLine("<table><thead><tr><th>ID</th><th>Mode</th><th>Status</th><th>Completed</th><th>Members</th><th>Duration</th></tr></thead><tbody>");
        foreach (var run in runs)
        {
            html.Append("<tr><td class=\"mono\">").Append(Escape(run.Id.ToString())).Append("</td>");
            html.Append("<td>").Append(Escape(run.Mode)).Append("</td>");
            html.Append("<td>").Append(Escape(run.Status)).Append("</td>");
            html.Append("<td class=\"mono\">").Append(Escape(IsRunning(run) ? "Running" : FormatTimestamp(run.CompletedAt!.Value))).Append("</td>");
            html.Append("<td class=\"mono\">").Append(Escape(run.MembersChecked.ToString("N0"))).Append("</td>");
            html.Append("<td class=\"mono\">").Append(Escape(FormatDuration(run.Duration))).AppendLine("</td></tr>");
        }

        html.AppendLine("</tbody></table>");
    }

    private static void AppendErrorTable(StringBuilder html, IReadOnlyList<IntegrationErrorSummary> errors)
    {
        if (errors.Count == 0)
        {
            html.AppendLine("<div class=\"ok\">No recent integration errors.</div>");
            return;
        }

        html.AppendLine("<table><thead><tr><th>Time</th><th>Source</th><th>Message</th></tr></thead><tbody>");
        foreach (var error in errors)
        {
            html.Append("<tr><td class=\"mono\">").Append(Escape(FormatTimestamp(error.CreatedAt))).Append("</td>");
            html.Append("<td>").Append(Escape(error.Source)).Append("</td>");
            html.Append("<td>").Append(Escape(error.Message)).AppendLine("</td></tr>");
        }

        html.AppendLine("</tbody></table>");
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    private static string FormatRunTimestamp(SyncRunSummary? run)
    {
        if (run is null)
        {
            return "No sync yet";
        }

        return run.CompletedAt is null
            ? $"Running since {FormatTimestamp(run.StartedAt)}"
            : IsRunning(run)
                ? $"Running since {FormatTimestamp(run.StartedAt)}"
            : FormatTimestamp(run.CompletedAt.Value);
    }

    private static bool IsRunning(SyncRunSummary run)
    {
        return string.Equals(run.Status, "Running", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDuration(TimeSpan value)
    {
        return value.TotalSeconds < 1
            ? $"{value.TotalMilliseconds:N0} ms"
            : value.ToString(@"mm\:ss");
    }

    private static int CountStatus(IReadOnlyList<CommandStatusCount> counts, string status)
    {
        return counts.FirstOrDefault(count => string.Equals(count.Status, status, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
