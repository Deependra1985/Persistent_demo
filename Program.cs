using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.EntityFrameworkCore;
using FileProcessingPOC.Data;
using FileProcessingPOC.Service;
using System.Text;
using System.IO;
using System.Globalization;
using Hangfire.Dashboard;
using DashboardOptions = Hangfire.DashboardOptions;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<FileDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<FileStatusService>();
builder.Services.AddScoped<FileWatcherService>();
builder.Services.AddTransient<FileProcessingJob>();

builder.Services.AddHangfire(config => config.UseMemoryStorage());
builder.Services.AddHangfireServer();

builder.WebHost.UseUrls("http://localhost:5000");

var app = builder.Build();

// Ensure DB exists
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FileDbContext>();
    db.Database.EnsureCreated();
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseRouting();

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();

    // CSS endpoint (unchanged)
    endpoints.MapGet("/hangfire/custom-hangfire.css", async context =>
    {
        var sbCss = new StringBuilder();
        sbCss.AppendLine("/* Styling for injected Files tab (if used elsewhere) */");
        sbCss.AppendLine(".navbar-nav > li > a[href='/hangfire/files'] {");
        sbCss.AppendLine("  padding: 10px 15px;");
        sbCss.AppendLine("  border-radius: 4px 4px 0 0;");
        sbCss.AppendLine("  border: 1px solid #ddd;");
        sbCss.AppendLine("  border-bottom: none;");
        sbCss.AppendLine("  background-color: #f9f9f9;");
        sbCss.AppendLine("  color: #333 !important;");
        sbCss.AppendLine("  text-decoration: none;");
        sbCss.AppendLine("  margin-right: 6px;");
        sbCss.AppendLine("}");
        sbCss.AppendLine(".navbar-nav > li > a[href='/hangfire/files']:hover {");
        sbCss.AppendLine("  background-color: #eee;");
        sbCss.AppendLine("}");
        context.Response.ContentType = "text/css";
        await context.Response.WriteAsync(sbCss.ToString());
    });

    // Enhanced /hangfire/files page: colored summary + data grid with paging
    endpoints.MapGet("/hangfire/files", async context =>
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileDbContext>();
        var allFiles = db.Files.AsNoTracking().ToList();

        // read paging params
        int page = 1;
        int pageSize = 25;
        if (int.TryParse(context.Request.Query["page"], out var p) && p > 0) page = p;
        if (int.TryParse(context.Request.Query["pageSize"], out var ps) && ps > 0) pageSize = ps;

        int total = allFiles.Count;
        int totalPages = (int)Math.Ceiling(total / (double)pageSize);
        if (page > totalPages) page = Math.Max(1, totalPages);

        // reflection helper for status detection
        string ReadStatus(object f)
        {
            if (f == null) return string.Empty;
            var t = f.GetType();
            var p = t.GetProperty("Status") ?? t.GetProperty("State") ?? t.GetProperty("FileStatus");
            return p?.GetValue(f)?.ToString() ?? string.Empty;
        }

        int pending = allFiles.Count(f => string.Equals(ReadStatus(f), "Pending", StringComparison.OrdinalIgnoreCase));
        int success = allFiles.Count(f => {
            var s = ReadStatus(f);
            return string.Equals(s, "Success", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "Completed", StringComparison.OrdinalIgnoreCase);
        });
        int failed = allFiles.Count(f => {
            var s = ReadStatus(f);
            return string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase);
        });

        // slice for current page
        var pageFiles = allFiles.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        int showingFrom = (pageFiles.Count == 0) ? 0 : ((page - 1) * pageSize) + 1;
        int showingTo = ((page - 1) * pageSize) + pageFiles.Count;

        // reflection helper to get properties flexibly
        string GetPropValue(object item, params string[] propNames)
        {
            if (item == null) return string.Empty;
            var t = item.GetType();
            foreach (var name in propNames)
            {
                var p = t.GetProperty(name);
                if (p != null)
                {
                    var val = p.GetValue(item);
                    if (val == null) return string.Empty;
                    if (val is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    return val.ToString();
                }
            }
            return string.Empty;
        }

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head><meta charset='utf-8' /><title>Files Dashboard</title>");
        // styles
        html.AppendLine("<style>");
        html.AppendLine("body{font-family:Segoe UI, Roboto, Arial, sans-serif;padding:18px;background:#f5f6f8;color:#222}");
        html.AppendLine(".summary-row{display:flex;gap:12px;align-items:center;margin-bottom:14px;flex-wrap:wrap}");
        html.AppendLine(".badge{padding:6px 10px;border-radius:12px;font-weight:600;color:#fff;display:inline-block;font-size:14px}");
        html.AppendLine(".badge.total{background:#6c757d}");
        html.AppendLine(".badge.pending{background:#f0ad4e;color:#2b2b2b}");
        html.AppendLine(".badge.success{background:#28a745}");
        html.AppendLine(".badge.failed{background:#dc3545}");
        // larger chart containers (wider)
        html.AppendLine(".charts-row{display:flex;gap:18px;flex-wrap:wrap;margin-bottom:18px}");
        html.AppendLine(".chart-container{width:520px;height:320px;background:#fff;padding:14px;border-radius:8px;box-shadow:0 1px 4px rgba(0,0,0,0.06)}");
        html.AppendLine("canvas{max-width:100%;height:auto}");
        // table
        html.AppendLine(".table-wrap{box-shadow:0 1px 6px rgba(0,0,0,0.04);border-radius:8px;padding:6px 6px 0 6px;background:#fff}");
        html.AppendLine("table{width:100%;border-collapse:collapse;min-width:900px}"); // min-width to encourage horizontal scroll if needed
        html.AppendLine("th,td{padding:12px 14px;text-align:left;border-bottom:1px solid #eee;font-size:13px}");
        html.AppendLine("th{background:#fafafa;font-weight:700}");
        html.AppendLine("tr.failed{background:rgba(220,53,69,0.06)}");
        html.AppendLine("tr.success{background:rgba(40,167,69,0.04)}");
        html.AppendLine("tr.pending{background:rgba(240,173,78,0.03)}");
        html.AppendLine(".status-pill{display:inline-block;padding:6px 10px;border-radius:12px;font-weight:600}");
        html.AppendLine(".status-pending{background:#fff3cd;color:#856404;border:1px solid #ffeeba}");
        html.AppendLine(".status-success{background:#d4edda;color:#155724;border:1px solid #c3e6cb}");
        html.AppendLine(".status-failed{background:#f8d7da;color:#721c24;border:1px solid #f5c6cb}");
        html.AppendLine(".muted{color:#666;font-size:12px}");
        html.AppendLine(".pager{display:flex;align-items:center;gap:8px;margin:12px 0;flex-wrap:wrap}");
        html.AppendLine(".pager a{padding:6px 10px;border-radius:6px;text-decoration:none;border:1px solid #ddd;color:#333;background:#fff}");
        html.AppendLine(".pager a.disabled{opacity:0.5;pointer-events:none}");
        html.AppendLine(".page-info{font-size:13px;color:#555;margin-left:8px}");
        html.AppendLine(".page-size{margin-left:12px}");
        html.AppendLine(".table-scroll{overflow-x:auto}");
        html.AppendLine("</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h2 style='margin-top:0;margin-bottom:6px'>📊 File Status Summary</h2>");

        // summary badges
        html.AppendLine("<div class='summary-row'>");
        html.AppendLine($"  <div class='badge total'>Total: {total}</div>");
        html.AppendLine($"  <div class='badge pending'>Pending: {pending}</div>");
        html.AppendLine($"  <div class='badge success'>Success: {success}</div>");
        html.AppendLine($"  <div class='badge failed'>Failed: {failed}</div>");
        html.AppendLine("</div>");

        // charts row (wider)
        html.AppendLine("<div class='charts-row'>");
        html.AppendLine("<div class='chart-container'><canvas id='pieChart' width='480' height='280'></canvas></div>");
        html.AppendLine("<div class='chart-container'><canvas id='trendChart' width='480' height='280'></canvas></div>");
        html.AppendLine("</div>");

        // pager + page size
        html.AppendLine("<div style='display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:8px'>");
        html.AppendLine("<div class='pager' role='navigation'>");
        var prevPage = page > 1 ? page - 1 : 1;
        var nextPage = page < totalPages ? page + 1 : totalPages;
        var basePath = "/hangfire/files";
        string MakeHref(int pg, int ps) => $"{basePath}?page={pg}&pageSize={ps}";

        html.AppendLine($"<a href='{(page > 1 ? MakeHref(prevPage, pageSize) : "#")}' class='{(page > 1 ? "" : "disabled")}'>&lt; Prev</a>");
        html.AppendLine($"<a href='{(page < totalPages ? MakeHref(nextPage, pageSize) : "#")}' class='{(page < totalPages ? "" : "disabled")}' style='margin-left:4px'>Next &gt;</a>");
        html.AppendLine($"<span class='page-info'>Showing {showingFrom}-{showingTo} of {total} — Page {page} of {Math.Max(1, totalPages)}</span>");
        html.AppendLine("</div>");

        // page size selector (simple links)
        html.AppendLine("<div class='page-size'>Show:");
        int[] sizes = new[] { 25, 50, 100 };
        foreach (var s in sizes)
        {
            var cls = s == pageSize ? "style='font-weight:700;margin-left:8px'" : "style='margin-left:8px'";
            html.AppendLine($" <a {cls} href='{basePath}?page=1&pageSize={s}'>{s}</a>");
        }
        html.AppendLine("</div>");
        html.AppendLine("</div>");

        // table
        html.AppendLine("<div class='table-wrap'>");
        html.AppendLine("<div class='table-scroll'>");
        html.AppendLine("<table>");
        html.AppendLine("<thead><tr>");
        html.AppendLine("<th style='width:60px'>ID</th>");
        html.AppendLine("<th>File</th>");
        html.AppendLine("<th style='width:140px'>Status</th>");
        html.AppendLine("<th style='width:180px'>Created</th>");
        html.AppendLine("<th style='width:180px'>Processed</th>");
        html.AppendLine("<th>Notes</th>");
        html.AppendLine("</tr></thead>");
        html.AppendLine("<tbody>");
        foreach (var f in pageFiles)
        {
            var status = GetPropValue(f, "Status", "State", "FileStatus");
            var id = GetPropValue(f, "Id", "ID", "FileId");
            var name = GetPropValue(f, "FileName", "Name", "Path");
            var created = GetPropValue(f, "CreatedAt", "CreatedOn", "Created");
            var processed = GetPropValue(f, "ProcessedAt", "CompletedAt", "ProcessedOn", "Processed");
            var note = GetPropValue(f, "ErrorMessage", "Message", "Notes", "Detail");

            var rowClass = "pending";
            if (!string.IsNullOrEmpty(status) && status.Equals("Success", StringComparison.OrdinalIgnoreCase)) rowClass = "success";
            if (!string.IsNullOrEmpty(status) && (status.Equals("Failed", StringComparison.OrdinalIgnoreCase) || status.Equals("Error", StringComparison.OrdinalIgnoreCase))) rowClass = "failed";

            var pillClass = "status-pending";
            if (!string.IsNullOrEmpty(status) && status.Equals("Success", StringComparison.OrdinalIgnoreCase)) pillClass = "status-success";
            if (!string.IsNullOrEmpty(status) && (status.Equals("Failed", StringComparison.OrdinalIgnoreCase) || status.Equals("Error", StringComparison.OrdinalIgnoreCase))) pillClass = "status-failed";

            html.AppendLine($"<tr class='{rowClass}'>");
            html.AppendLine($"  <td>{System.Net.WebUtility.HtmlEncode(id)}</td>");
            html.AppendLine($"  <td><div style='font-weight:600;color:#222'>{System.Net.WebUtility.HtmlEncode(name)}</div><div class='muted'>{System.Net.WebUtility.HtmlEncode(GetPropValue(f, "Path", "Directory"))}</div></td>");
            html.AppendLine($"  <td><span class='status-pill {pillClass}'>{System.Net.WebUtility.HtmlEncode(status)}</span></td>");
            html.AppendLine($"  <td>{System.Net.WebUtility.HtmlEncode(created)}</td>");
            html.AppendLine($"  <td>{System.Net.WebUtility.HtmlEncode(processed)}</td>");
            html.AppendLine($"  <td>{System.Net.WebUtility.HtmlEncode(note)}</td>");
            html.AppendLine("</tr>");
        }
        if (!pageFiles.Any())
        {
            html.AppendLine("<tr><td colspan='6' style='text-align:center;padding:18px;color:#666'>No files to show on this page</td></tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</div>"); // table-scroll
        html.AppendLine("</div>"); // table-wrap

        // pager repeated at bottom
        html.AppendLine("<div style='display:flex;justify-content:space-between;align-items:center;gap:12px;margin-top:10px;flex-wrap:wrap'>");
        html.AppendLine("<div class='pager'>");
        html.AppendLine($"<a href='{(page > 1 ? MakeHref(prevPage, pageSize) : "#")}' class='{(page > 1 ? "" : "disabled")}'>&lt; Prev</a>");
        html.AppendLine($"<a href='{(page < totalPages ? MakeHref(nextPage, pageSize) : "#")}' class='{(page < totalPages ? "" : "disabled")}' style='margin-left:6px'>Next &gt;</a>");
        html.AppendLine($"<span class='page-info'>Showing {showingFrom}-{showingTo} of {total} — Page {page} of {Math.Max(1, totalPages)}</span>");
        html.AppendLine("</div>");
        html.AppendLine("</div>");

        // JS: Chart.js and auto-refresh counts
        html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js'></script>");
        html.AppendLine("<script>");
        html.AppendLine(" (function(){");
        html.AppendLine($"  const initialPending = {pending};");
        html.AppendLine($"  const initialSuccess = {success};");
        html.AppendLine($"  const initialFailed = {failed};");
        html.AppendLine("  const pieCtx = document.getElementById('pieChart').getContext('2d');");
        html.AppendLine("  const trendCtx = document.getElementById('trendChart').getContext('2d');");
        html.AppendLine("  const pieChart = new Chart(pieCtx, { type: 'pie', data: { labels: ['Pending','Success','Failed'], datasets: [{ data: [initialPending, initialSuccess, initialFailed], backgroundColor: ['#F0AD4E', '#28A745', '#DC3545'], borderColor: '#fff', borderWidth: 2 }] }, options: { plugins:{legend:{position:'bottom'}}, responsive:true } });");
        html.AppendLine("  async function loadTrend(){");
        html.AppendLine("    try { const res = await fetch('/api/filestatus/trend'); if (!res.ok) return; const data = await res.json(); if (!data || !data.length) return; const labels = data.map(x=>x.time); const pending = data.map(x=>x.pending); const success = data.map(x=>x.success); const failed = data.map(x=>x.failed); new Chart(trendCtx, { type:'line', data:{ labels: labels, datasets: [ { label: 'Pending', data: pending, borderColor: '#F0AD4E', backgroundColor: 'rgba(240,173,78,0.18)', fill:true }, { label: 'Success', data: success, borderColor: '#28A745', backgroundColor: 'rgba(40,167,69,0.12)', fill:true }, { label: 'Failed', data: failed, borderColor: '#DC3545', backgroundColor: 'rgba(220,53,69,0.12)', fill:true } ] }, options:{responsive:true, plugins:{legend:{position:'bottom'}}} }); } catch(e) { console && console.warn && console.warn('trend fetch failed', e); }");
        html.AppendLine("  }");
        html.AppendLine("  async function refreshCounts(){");
        html.AppendLine("    try { const res = await fetch('/api/filestatus/summary'); if(!res.ok) return; const d = await res.json(); if(!d) return; document.querySelector('.badge.total').textContent = 'Total: ' + d.total; document.querySelector('.badge.pending').textContent = 'Pending: ' + d.pending; document.querySelector('.badge.success').textContent = 'Success: ' + d.success; document.querySelector('.badge.failed').textContent = 'Failed: ' + d.failed; pieChart.data.datasets[0].data = [d.pending,d.success,d.failed]; pieChart.update(); } catch(e){} }");
        html.AppendLine("  loadTrend(); refreshCounts(); setInterval(()=>{ refreshCounts(); }, 10000);");
        html.AppendLine("})();");
        html.AppendLine("</script>");

        html.AppendLine("</body></html>");

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html.ToString());
    });

    // wrapper UI at /hangfire-ui (fixed CSS and layout)
    endpoints.MapGet("/hangfire-ui", async context =>
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head><meta charset='utf-8' /><title>Hangfire - UI</title>");
        html.AppendLine("<meta name='viewport' content='width=device-width,initial-scale=1' />");
        html.AppendLine("<style>");
        // explicit topbar height and robust flexbox iframe container
        html.AppendLine("html,body{height:100%;margin:0;}");
        html.AppendLine("body{font-family:Segoe UI, Roboto, Arial, sans-serif;margin:0;height:100vh;display:flex;flex-direction:column}");
        html.AppendLine(".topbar{background:#fff;border-bottom:1px solid #e6e6e6;height:56px;display:flex;align-items:center;padding:0 12px;gap:12px;box-sizing:border-box}");
        html.AppendLine(".brand{font-size:16px;font-weight:600}");
        html.AppendLine(".tabs{display:flex;gap:6px;margin-left:16px}");
        html.AppendLine(".tabs a{padding:8px 12px;text-decoration:none;color:#333;border-radius:4px;border:1px solid transparent}");
        html.AppendLine(".tabs a.active{background:#f1f1f1;border-color:#ddd}");
        html.AppendLine(".iframe-wrap{flex:1;min-height:0;display:block}"); // min-height:0 is important for flex children
        html.AppendLine("iframe{width:100%;height:100%;border:0;display:block}");
        html.AppendLine("</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<div class='topbar'>");
        html.AppendLine("  <div class='brand'>📁 File Processing - Dashboard</div>");
        html.AppendLine("  <div class='tabs' id='tabs'>");
        html.AppendLine("    <a href='#' data-src='/hangfire/jobs/enqueued' class='tab active'>Jobs</a>");
        html.AppendLine("    <a href='#' data-src='/hangfire/retries' class='tab'>Retries</a>");
        html.AppendLine("    <a href='#' data-src='/hangfire/recurring' class='tab'>Recurring</a>");
        html.AppendLine("    <a href='#' data-src='/hangfire/servers' class='tab'>Servers</a>");
        html.AppendLine("    <a href='#' data-src='/hangfire/files' class='tab'>Files</a>");
        html.AppendLine("  </div>");
        html.AppendLine("</div>");
        html.AppendLine("<div class='iframe-wrap'><iframe id='dashFrame' src='/hangfire/jobs/enqueued'></iframe></div>");
        html.AppendLine("<script>");
        html.AppendLine("  (function(){");
        html.AppendLine("    var tabs = document.querySelectorAll('.tabs a.tab');");
        html.AppendLine("    var frame = document.getElementById('dashFrame');");
        html.AppendLine("    tabs.forEach(function(t){ t.addEventListener('click', function(e){ e.preventDefault(); tabs.forEach(x=>x.classList.remove('active')); this.classList.add('active'); var src = this.getAttribute('data-src'); frame.src = src; }); });");
        html.AppendLine("    frame.addEventListener('load', function(){");
        html.AppendLine("      try {");
        html.AppendLine("        var doc = frame.contentDocument || frame.contentWindow.document;");
        html.AppendLine("        if (!doc) return;");
        html.AppendLine("        var nav = doc.querySelector('.navbar'); if (nav) nav.style.display = 'none';");
        html.AppendLine("        var footer = doc.getElementById('footer'); if (footer) footer.style.display = 'none';");
        html.AppendLine("        var wrap = doc.getElementById('wrap'); if (wrap) wrap.style.paddingTop = '0';");
        html.AppendLine("        var container = doc.querySelector('.js-page-container'); if (container) container.style.marginTop = '0';");
        html.AppendLine("      } catch(e) { console && console.warn && console.warn('iframe cleanup failed', e); }");
        html.AppendLine("    });");
        html.AppendLine("    setInterval(function(){ try { var doc = frame.contentDocument || frame.contentWindow.document; if(!doc) return; var nav = doc.querySelector('.navbar'); if(nav && nav.style.display !== 'none') nav.style.display='none'; } catch(e){} }, 1000);");
        html.AppendLine("  })();");
        html.AppendLine("</script>");
        html.AppendLine("</body></html>");

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html.ToString());
    });
});

// register hangfire dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    AppPath = null,
    DisplayStorageConnectionString = false,
    DashboardTitle = "📁 File Processing Dashboard",
    AsyncAuthorization = new[] { new Hangfire.Dashboard.NoAuthorizationFilter() },
});

// start watcher
using (var scope = app.Services.CreateScope())
{
    var watcher = scope.ServiceProvider.GetRequiredService<FileWatcherService>();
    watcher.StartWatching();
}

app.Run();
