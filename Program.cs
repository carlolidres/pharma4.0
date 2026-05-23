using System.Security.Claims;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.OpenApi.Models;
using Microsoft.IdentityModel.Tokens;
using Pharma40.Services;
using Pharma40.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Pharma 4.0 Validation Management API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter JWT bearer token."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        }] = []
    });
});
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".Pharma40.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(8);
});
builder.Services.AddDataProtection();
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey)),
            ClockSkew = TimeSpan.FromMinutes(1),
            RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                RecordSecurityAudit(context.HttpContext, "Unauthorized access attempt", context.Error ?? "Unauthorized", context.ErrorDescription ?? "Missing or invalid bearer token.");
                return Task.CompletedTask;
            },
            OnForbidden = context =>
            {
                RecordSecurityAudit(context.HttpContext, "Forbidden access attempt", context.HttpContext.User.FindFirstValue(ClaimTypes.Email) ?? "Authenticated user", "Role or password-change policy blocked access.");
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Administrator"));
    options.AddPolicy("ManagerOrAdmin", policy => policy.RequireRole("Administrator", "Manager"));
});

builder.Services.AddSingleton<JsonDataStore>();
builder.Services.Configure<EmailProviderOptions>(builder.Configuration.GetSection("EmailProvider"));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<GoogleGmailApiOptions>(builder.Configuration.GetSection("GoogleGmailApi"));
builder.Services.AddSingleton<EmailTemplateService>();
builder.Services.AddSingleton<IEmailProviderService, SmtpEmailService>();
builder.Services.AddSingleton<IEmailProviderService, GmailApiEmailService>();
builder.Services.AddSingleton<IEmailService, EmailServiceSelector>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<RefreshTokenService>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddSingleton<UserAccessService>();
builder.Services.AddSingleton<TCodeService>();
builder.Services.AddSingleton<DraftService>();
builder.Services.AddSingleton<CnfService>();
builder.Services.AddSingleton<VrmsRoutingService>();
builder.Services.AddSingleton<ArchiveService>();
builder.Services.AddSingleton<IDocxEditorAdapter, LocalDocxPreviewAdapter>();
builder.Services.AddSingleton<LibreOfficeConversionService>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<ArchiveMaintenanceService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseStaticFiles();
app.UseSession();

app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (IsTCodePagePath(context.Request.Path))
    {
        var authService = context.RequestServices.GetRequiredService<AuthService>();
        var accessService = context.RequestServices.GetRequiredService<UserAccessService>();
        var user = CurrentUser(authService, context);
        var requiredTCode = TCodeForPath(context.Request.Path);
        if (user is null)
        {
            context.Response.Redirect("/Auth");
            return;
        }

        if (!accessService.HasTCodeAccess(user, requiredTCode))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<!doctype html><html><head><title>Access Denied</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:0;display:grid;place-items:center;min-height:100vh;background:#f8fafc;color:#0f172a}.card{border:1px solid #dbe3ef;border-radius:16px;background:white;padding:28px;max-width:520px;box-shadow:0 18px 45px rgba(15,23,42,.08)}a{color:#2563eb;font-weight:700}</style></head><body><main class='card'><h1>Access Denied</h1><p>You do not have access to this T-code. Ask an administrator to assign access.</p><a href='/Dashboard'>Return to Launch Pad</a></main></body></html>");
            RecordSecurityAudit(context, "Forbidden direct T-code access", user.Email, requiredTCode);
            return;
        }
    }

    if (context.Request.Path.StartsWithSegments("/api") &&
        context.User.Identity?.IsAuthenticated == true &&
        string.Equals(context.User.FindFirstValue("mustChangePassword"), "true", StringComparison.OrdinalIgnoreCase) &&
        !IsPasswordChangeAllowedPath(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { success = false, message = "Password change required before accessing this module." });
        RecordSecurityAudit(context, "Forbidden access attempt", context.User.FindFirstValue(ClaimTypes.Email) ?? "Authenticated user", "Password change required before accessing this module.");
        return;
    }

    if (IsTCodeApiPath(context.Request.Path))
    {
        var authService = context.RequestServices.GetRequiredService<AuthService>();
        var accessService = context.RequestServices.GetRequiredService<UserAccessService>();
        var user = CurrentUser(authService, context);
        var tcode = TCodeForApiPath(context.Request.Path);
        var action = ActionForMethod(context.Request.Method);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Session expired." });
            return;
        }

        if (!accessService.HasAction(user, tcode, action))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Access denied for this T-code operation." });
            RecordSecurityAudit(context, "Forbidden T-code API access", user.Email, $"{tcode}:{action}");
            return;
        }
    }

    await next();
});
app.UseAuthorization();

app.MapStaticAssets();
MapDraftApi(app);
MapCnfApi(app);
MapRegistryApi(app);
MapUserAccessApi(app);
MapAuthApi(app);
MapNotificationApi(app);
MapVrmsApi(app);
MapAccountManagementApi(app);
MapArchiveApi(app);
MapProfileApi(app);
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

static bool IsPasswordChangeAllowedPath(PathString path) =>
    path.StartsWithSegments("/api/auth/change-temporary-password") ||
    path.StartsWithSegments("/api/auth/me") ||
    path.StartsWithSegments("/api/auth/totp") ||
    path.StartsWithSegments("/api/auth/login") ||
    path.StartsWithSegments("/api/auth/forgot-password/reset-with-authenticator");

static bool IsTCodePagePath(PathString path) =>
    path.StartsWithSegments("/Draft") ||
    path.StartsWithSegments("/Documents") ||
    path.StartsWithSegments("/CNF") ||
    path.StartsWithSegments("/REG") ||
    path.StartsWithSegments("/UserManagement");

static string TCodeForPath(PathString path)
{
    if (path.StartsWithSegments("/Draft")) return "DRAFT";
    if (path.StartsWithSegments("/Documents")) return "VRMS";
    if (path.StartsWithSegments("/CNF")) return "CNF";
    if (path.StartsWithSegments("/REG")) return "REG";
    if (path.StartsWithSegments("/UserManagement")) return "USER";
    return "";
}

static bool IsTCodeApiPath(PathString path) =>
    path.StartsWithSegments("/api/drafts") ||
    path.StartsWithSegments("/api/cnf") ||
    path.StartsWithSegments("/api/reg") ||
    path.StartsWithSegments("/api/vrms");

static string TCodeForApiPath(PathString path)
{
    if (path.StartsWithSegments("/api/drafts")) return "DRAFT";
    if (path.StartsWithSegments("/api/cnf")) return "CNF";
    if (path.StartsWithSegments("/api/reg")) return "REG";
    if (path.StartsWithSegments("/api/vrms")) return "VRMS";
    return "";
}

static string ActionForMethod(string method) => method.ToUpperInvariant() switch
{
    "GET" => "view",
    "POST" => "create",
    "PUT" or "PATCH" => "edit",
    "DELETE" => "archive",
    _ => "view"
};

static void RecordSecurityAudit(HttpContext context, string action, string performedBy, string remarks)
{
    try
    {
        var store = context.RequestServices.GetRequiredService<JsonDataStore>();
        var data = store.Read();
        data.AuditTrail.Add(new AuditTrail
        {
            AuditTrailId = data.NextAuditTrailId++,
            EntityName = "Security",
            EntityId = context.Request.Path,
            Action = action,
            PerformedBy = performedBy,
            PerformedAt = DateTime.UtcNow,
            IPAddress = context.Connection.RemoteIpAddress?.ToString() ?? "",
            DeviceInfo = context.Request.Headers.UserAgent.ToString(),
            Remarks = remarks
        });
        store.Write(data);
    }
    catch
    {
        // Authentication event logging must not interfere with the HTTP auth response.
    }
}

static void MapArchiveApi(WebApplication app)
{
    var archive = app.MapGroup("/api/archive").RequireAuthorization();

    archive.MapGet("/", (string? module, string? search, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var query = store.Read().ArchivedRecords.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(module)) query = query.Where(item => item.SourceModule.Equals(module, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(item => item.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.DisplayKey.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return Results.Json(new { success = true, items = query.OrderByDescending(item => item.ArchivedAtUtc).Take(500) });
    });

    archive.MapPost("/run", (ArchiveService archiveService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (!user.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var count = archiveService.RunAnnualArchive("System", "Manual annual archive run", context);
        return Results.Json(new { success = true, archived = count });
    }).RequireAuthorization("AdminOnly");
}

static void MapProfileApi(WebApplication app)
{
    var profile = app.MapGroup("/api/profile").RequireAuthorization();

    profile.MapGet("/", (AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null ? Results.Unauthorized() : Results.Json(new { success = true, profile = AccountDto(user) });
    });

    profile.MapPost("/", async (HttpRequest request, JsonDataStore store, AuthService authService, IHttpClientFactory httpClientFactory, IDataProtectionProvider protectionProvider, IConfiguration configuration, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.BadRequest(new { success = false, message = "Multipart form data is required." });

        var form = await request.ReadFormAsync();
        var data = store.Read();
        var user = data.Users.FirstOrDefault(item => item.Id == current.Id);
        if (user is null) return Results.Unauthorized();

        var oldValue = $"{user.FullName}|{user.EmployeeIdNo}|{user.Department}|{user.Position}";
        user.FullName = form["name"].ToString().Trim();
        user.EmployeeIdNo = form["companyId"].ToString().Trim();
        user.Department = form["department"].ToString().Trim();
        user.Position = form["title"].ToString().Trim();
        user.UpdatedAt = DateTime.UtcNow;
        data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "User profile update", user.Email, context, oldValue, $"{user.FullName}|{user.EmployeeIdNo}|{user.Department}|{user.Position}", ""));

        var photo = form.Files["photoId"];
        if (photo is not null && photo.Length > 0)
        {
            var photoResult = await ReadImageDataUrl(photo, ["image/png", "image/jpeg", "image/jpg", "image/webp"], 5 * 1024 * 1024);
            if (!photoResult.Success) return Results.BadRequest(new { success = false, message = photoResult.Message });
            var hadPhoto = !string.IsNullOrWhiteSpace(user.PhotoIdDataUrl);
            user.PhotoIdDataUrl = photoResult.DataUrl;
            user.ProfilePhotoDataUrl = photoResult.DataUrl;
            data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), hadPhoto ? "Photo ID upload/update" : "Photo ID upload/update", user.Email, context, hadPhoto ? "Existing photo ID" : "", "Photo ID saved", ""));
        }

        var signature = form.Files["signature"];
        var signatureMessage = "";
        if (signature is not null && signature.Length > 0)
        {
            if (!signature.ContentType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { success = false, message = "Signature must be uploaded in PNG format." });
            }
            if (signature.Length > 2 * 1024 * 1024) return Results.BadRequest(new { success = false, message = "Signature PNG must be 2 MB or smaller." });

            await using var stream = signature.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var bytes = memory.ToArray();
            if (PngHasTransparency(bytes))
            {
                user.SignatureDataUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
                user.SignatureBackgroundWarning = false;
                signatureMessage = "Signature saved.";
                data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "Signature upload/update", user.Email, context, "", "Transparent PNG signature saved", ""));
            }
            else
            {
                data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "Signature background removal processing", user.Email, context, "", "remove.bg processing requested", "The uploaded signature appears to have a background. Processing background removal..."));
                var removeResult = await RemoveSignatureBackground(bytes, store, httpClientFactory, protectionProvider, configuration, user.Email, context, data);
                if (removeResult.Success)
                {
                    user.SignatureDataUrl = $"data:image/png;base64,{Convert.ToBase64String(removeResult.Bytes)}";
                    user.SignatureBackgroundWarning = false;
                    signatureMessage = "Signature background removed successfully.";
                    data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "Signature upload/update", user.Email, context, "Visible background", "Transparent PNG signature saved", signatureMessage));
                }
                else
                {
                    user.SignatureBackgroundWarning = true;
                    signatureMessage = removeResult.Message;
                    data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "Failed signature background removal attempt", user.Email, context, "Visible background", "Signature rejected", removeResult.Message));
                    store.Write(data);
                    return Results.BadRequest(new { success = false, message = removeResult.Message });
                }
            }
        }

        store.Write(data);
        return Results.Json(new { success = true, message = string.IsNullOrWhiteSpace(signatureMessage) ? "Profile saved." : signatureMessage, profile = AccountDto(user) });
    });
}

static void MapDraftApi(WebApplication app)
{
    var drafts = app.MapGroup("/api/drafts").RequireAuthorization();

    drafts.MapGet("/", (DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null
            ? Results.Unauthorized()
            : Results.Json(new { success = true, items = draftService.GetDocuments() });
    });

    drafts.MapGet("/{id:int}", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null
            ? Results.Unauthorized()
            : Results.Json(new { success = true, data = draftService.GetWorkspace(id) });
    });

    drafts.MapPost("/{id:int}/generate-preview", async (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = await draftService.GeneratePreviewAsync(id, user, context);
        return Results.Json(new { success = result.Success, message = result.Message, previewUrl = result.PreviewUrl }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapGet("/{id:int}/preview", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.GetPreviewFile(id, user, context);
        return result.Success
            ? Results.File(result.Path, "application/pdf", enableRangeProcessing: true)
            : Results.NotFound(new { success = false, message = result.Message });
    });

    drafts.MapGet("/{id:int}/download-original", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.DownloadOriginal(id, user, context);
        return result.Success
            ? Results.File(result.Path, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", result.DownloadName)
            : Results.NotFound(new { success = false, message = result.Message });
    });

    drafts.MapGet("/{id:int}/versions/{versionId:int}/preview", (int id, int versionId, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.GetVersionPreviewFile(id, versionId, user, context);
        return result.Success
            ? Results.File(result.Path, "application/pdf", enableRangeProcessing: true)
            : Results.NotFound(new { success = false, message = result.Message });
    });

    drafts.MapGet("/by-tracker/{draftTrackerNo}", (string draftTrackerNo, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var item = draftService.GetDocuments().FirstOrDefault(draft => draft.DraftTrackerNo.Equals(draftTrackerNo, StringComparison.OrdinalIgnoreCase));
        return item is null ? Results.NotFound(new { success = false, message = "Draft tracker not found." }) : Results.Json(new { success = true, item });
    });

    drafts.MapGet("/registry", (DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var users = draftService.ActiveUsers().Select(item => new { item.UserCode, item.FullName, item.Email, item.Role });
        return Results.Json(new
        {
            success = true,
            statuses = Pharma40.Models.DraftStatuses.All,
            categories = new[] { "Process", "Non-process", "APR", "Protocol", "Report", "Others" },
            reportProtocols = new[] { "Protocol", "Report", "Validation Protocol", "Validation Report", "Verification Protocol", "Verification Report", "APR", "Endorsement", "Investigation Report", "Others" },
            clients = new[] { "NA", "Haleon", "Pfizer Inc.", "Bayer Philippines, Inc. - Pharma Division", "Interphil Laboratories Inc.", "iNova Pharmaceuticals Philippines", "JNTL Consumer Health (Philippines), Inc." },
            departments = new[] { "DPM", "DPP", "LPM", "LPP", "QA", "QC Plant 1", "QC Plant 2", "RnD", "Engineering", "IT", "Other" },
            activeUsers = users
        });
    });

    drafts.MapPost("/upload", async (HttpRequest request, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.BadRequest(new { success = false, message = "Multipart form data is required." });
        var form = await request.ReadFormAsync();
        var file = form.Files["file"];
        var upload = new DraftUploadRequest
        {
            DocumentTitle = form["documentTitle"].ToString(),
            EquipmentProduct = form["equipmentProduct"].ToString(),
            Category = form["category"].ToString(),
            ReportProtocol = form["reportProtocol"].ToString(),
            ClientName = form["clientName"].ToString(),
            Department = form["department"].ToString(),
            PreparedBy = form["preparedBy"].ToString(),
            CheckedBy = form["checkedBy"].ToString(),
            Remarks = form["remarks"].ToString()
        };
        var result = await draftService.UploadAsync(file!, upload, user, context);
        return Results.Json(new
        {
            success = result.Success,
            message = result.Message,
            document = result.Document,
            previewUrl = result.Document is null ? "" : $"/api/drafts/{result.Document.DraftDocumentId}/preview",
            originalUrl = result.Document is null ? "" : $"/api/drafts/{result.Document.DraftDocumentId}/download-original"
        }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPut("/{id:int}", async (int id, HttpRequest request, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var existing = draftService.GetDocument(id);
        return existing is null
            ? Results.NotFound(new { success = false, message = "Draft not found." })
            : Results.Json(new { success = true, message = "Metadata update endpoint reserved for SQL-backed implementation.", document = existing });
    });

    drafts.MapPost("/{id:int}/route-for-review", async (int id, HttpRequest request, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<DraftRouteRequest>() ?? new DraftRouteRequest();
        var result = draftService.RouteForReview(id, payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/comments", async (int id, HttpRequest request, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<DraftCommentRequest>() ?? new DraftCommentRequest();
        var result = draftService.AddComment(id, payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/proposed-changes", async (int id, HttpRequest request, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<DraftChangeRequest>() ?? new DraftChangeRequest();
        var result = draftService.AddProposedChange(id, payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/proposed-changes/{changeId:int}/accept", (int id, int changeId, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.DispositionChange(id, changeId, true, null, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/proposed-changes/{changeId:int}/reject", async (int id, int changeId, HttpRequest request, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>() ?? [];
        payload.TryGetValue("reason", out var reason);
        var result = draftService.DispositionChange(id, changeId, false, reason, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/complete-review", async (int id, HttpRequest request, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>() ?? [];
        var result = draftService.CompleteReview(id, payload.GetValueOrDefault("remarks", ""), user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/comments/{commentId:int}/resolve", (int id, int commentId, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.ResolveComment(id, commentId, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/generate-controlled-version", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.GenerateControlledVersion(id, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/lock-final-version", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.LockOrConvert(id, false, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/convert-to-pdf", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.LockOrConvert(id, true, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/transfer-to-vrms", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.TransferToVrms(id, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/archive", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.ArchiveOrCancel(id, "Archived", user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });

    drafts.MapPost("/{id:int}/cancel", (int id, DraftService draftService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = draftService.ArchiveOrCancel(id, "Cancelled", user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });
}

static void MapCnfApi(WebApplication app)
{
    var cnf = app.MapGroup("/api/cnf").RequireAuthorization();

    cnf.MapGet("/", (CnfService cnfService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null
            ? Results.Unauthorized()
            : Results.Json(new { success = true, data = cnfService.GetWorkspace() });
    });

    cnf.MapPost("/", async (HttpRequest request, CnfService cnfService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<CnfSaveRequest>() ?? new CnfSaveRequest();
        var result = cnfService.Save(payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message, record = result.Record }, statusCode: result.Success ? 200 : 400);
    });

    cnf.MapPut("/{id:int}", async (int id, HttpRequest request, CnfService cnfService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<CnfSaveRequest>() ?? new CnfSaveRequest();
        payload.Record.CnfRecordId = id;
        var result = cnfService.Save(payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message, record = result.Record }, statusCode: result.Success ? 200 : 400);
    });

    cnf.MapPost("/route", async (HttpRequest request, CnfService cnfService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<CnfRouteRequest>() ?? new CnfRouteRequest();
        var result = cnfService.TriggerRouting(payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message, trigger = result.Trigger, url = result.Url }, statusCode: result.Success ? 200 : 409);
    });

    cnf.MapPost("/close", async (HttpRequest request, CnfService cnfService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<CnfCloseRequest>() ?? new CnfCloseRequest();
        var result = cnfService.Close(payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message, openBranches = result.OpenBranches }, statusCode: result.Success ? 200 : 400);
    });

    cnf.MapPost("/import", async (HttpRequest request, CnfService cnfService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<CnfImportRequest>() ?? new CnfImportRequest();
        var result = cnfService.Import(payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message, summary = result.Summary }, statusCode: result.Success ? 200 : 400);
    });

    cnf.MapPost("/sync-status", async (HttpRequest request, CnfService cnfService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<CnfStatusSyncRequest>() ?? new CnfStatusSyncRequest();
        var result = cnfService.SyncDocumentStatus(payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });
}

static void MapRegistryApi(WebApplication app)
{
    var reg = app.MapGroup("/api/reg").RequireAuthorization();

    reg.MapGet("/workspace", (RegistryService registryService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null ? Results.Unauthorized() : Results.Json(new { success = true, data = registryService.GetWorkspace() });
    });

    reg.MapGet("/{categoryKey}", (string categoryKey, string? module, RegistryService registryService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null ? Results.Unauthorized() : Results.Json(new { success = true, items = registryService.GetRegistry(categoryKey, module ?? "") });
    });

    reg.MapGet("/clients/list", (RegistryService registryService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null ? Results.Unauthorized() : Results.Json(new { success = true, items = registryService.GetRegistry("clients") });
    });

    reg.MapGet("/departments/list", (RegistryService registryService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null ? Results.Unauthorized() : Results.Json(new { success = true, items = registryService.GetRegistry("departments") });
    });

    reg.MapGet("/workflow-statuses/list", (RegistryService registryService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        return user is null ? Results.Unauthorized() : Results.Json(new { success = true, items = registryService.GetRegistry("validation-workflow-statuses") });
    });

    reg.MapPost("/entries", async (HttpRequest request, RegistryService registryService, AuthService authService, UserAccessService accessService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (!accessService.HasAction(user, "REG", "configure")) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<RegEntrySaveRequest>() ?? new RegEntrySaveRequest();
        var result = registryService.SaveEntry(payload, user, context);
        return Results.Json(new { success = result.Success, message = result.Message, entry = result.Entry }, statusCode: result.Success ? 200 : 400);
    });

    reg.MapPost("/entries/{id:int}/active", async (int id, HttpRequest request, RegistryService registryService, AuthService authService, UserAccessService accessService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (!accessService.HasAction(user, "REG", "configure")) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<RegEntryStatusRequest>() ?? new RegEntryStatusRequest();
        var result = registryService.SetEntryActive(id, payload.IsActive, user, context);
        return Results.Json(new { success = result.Success, message = result.Message }, statusCode: result.Success ? 200 : 400);
    });
}

static void MapUserAccessApi(WebApplication app)
{
    var users = app.MapGroup("/api/user-access").RequireAuthorization();

    users.MapGet("/workspace", (UserAccessService accessService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (!user.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || !accessService.HasTCodeAccess(user, "USER")) return Results.Forbid();
        return Results.Json(new { success = true, data = accessService.GetWorkspace() });
    });

    users.MapGet("/current", (UserAccessService accessService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        return Results.Json(new
        {
            success = true,
            permissions = new
            {
                canConfigureRegistry = accessService.HasAction(user, "REG", "configure"),
                canCloseCnf = accessService.HasAction(user, "CNF", "close"),
                canRoute = accessService.HasAction(user, "VRMS", "route")
            }
        });
    });

    users.MapPost("/tcode-access", async (HttpRequest request, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<TCodeAccessSaveRequest>() ?? new TCodeAccessSaveRequest();
        var data = store.Read();
        var affected = data.Users.FirstOrDefault(item => item.Id == payload.UserId);
        if (affected is null) return Results.NotFound(new { success = false, message = "User not found." });
        if (!data.TCodes.Any(item => item.Code.Equals(payload.TCode, StringComparison.OrdinalIgnoreCase))) return Results.NotFound(new { success = false, message = "T-code not found." });
        var normalizedAccess = NormalizeTCodeAccess(payload.AccessLevel);
        var normalizedTCode = payload.TCode.Trim().ToUpperInvariant();
        var assignment = data.UserTCodeAccess.FirstOrDefault(item => item.PrincipalType == "User" && item.PrincipalKey == payload.UserId.ToString() && item.TCode.Equals(normalizedTCode, StringComparison.OrdinalIgnoreCase));
        var action = "T-code assigned";
        var oldValue = "";
        if (assignment is null)
        {
            assignment = new UserTCodeAccess { UserTCodeAccessId = data.NextUserTCodeAccessId++, PrincipalType = "User", PrincipalKey = payload.UserId.ToString(), TCode = normalizedTCode };
            data.UserTCodeAccess.Add(assignment);
        }
        else
        {
            oldValue = $"{assignment.TCode}|{assignment.AccessLevel}|{assignment.IsActive}";
            action = payload.IsActive ? "T-code access modified" : "T-code access removed";
        }

        assignment.AccessLevel = normalizedAccess;
        assignment.IsActive = payload.IsActive && !normalizedAccess.Equals("No Access", StringComparison.OrdinalIgnoreCase);
        data.AuditTrail.Add(AuditRow(data, "TCodeAccess", affected.Id.ToString(), action, current.Email, context, oldValue, $"{assignment.TCode}|{assignment.AccessLevel}|{assignment.IsActive}", payload.Remarks));
        store.Write(data);
        return Results.Json(new { success = true, assignment });
    });

    users.MapPut("/password-policy", async (HttpRequest request, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<PasswordPolicySettings>() ?? new PasswordPolicySettings();
        var data = store.Read();
        var oldValue = System.Text.Json.JsonSerializer.Serialize(data.PasswordPolicy);
        data.PasswordPolicy = payload;
        data.AuditTrail.Add(AuditRow(data, "Security", "PasswordPolicy", "Password policy changed", current.Email, context, oldValue, System.Text.Json.JsonSerializer.Serialize(payload), ""));
        store.Write(data);
        return Results.Json(new { success = true, passwordPolicy = payload });
    });

    users.MapPut("/security-parameters", async (HttpRequest request, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<SecurityParameterSettings>() ?? new SecurityParameterSettings();
        var data = store.Read();
        var oldValue = System.Text.Json.JsonSerializer.Serialize(data.SecurityParameters);
        data.SecurityParameters = payload;
        data.AuditTrail.Add(AuditRow(data, "Security", "SecurityParameters", "Security parameter changed", current.Email, context, oldValue, System.Text.Json.JsonSerializer.Serialize(payload), ""));
        store.Write(data);
        return Results.Json(new { success = true, securityParameters = payload });
    });

    users.MapGet("/api-management", (JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var data = store.Read();
        EnsureApiCredentialSeed(data);
        store.Write(data);
        return Results.Json(new
        {
            success = true,
            items = data.ApiCredentials.OrderBy(item => item.ServiceName).Select(item => new
            {
                item.ApiCredentialId,
                item.ServiceName,
                item.MaskedApiKey,
                item.Status,
                item.LastUpdatedAtUtc,
                item.UpdatedBy
            })
        });
    }).RequireAuthorization("AdminOnly");

    users.MapPost("/api-management", async (HttpRequest request, JsonDataStore store, AuthService authService, IDataProtectionProvider protectionProvider, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<ApiCredentialSaveRequest>() ?? new ApiCredentialSaveRequest();
        var service = string.IsNullOrWhiteSpace(payload.ServiceName) ? "remove.bg" : payload.ServiceName.Trim();
        if (!service.Equals("remove.bg", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { success = false, message = "Only remove.bg is currently supported." });
        if (string.IsNullOrWhiteSpace(payload.ApiKey)) return Results.BadRequest(new { success = false, message = "API key is required." });
        var data = store.Read();
        var item = data.ApiCredentials.FirstOrDefault(row => row.ServiceName.Equals(service, StringComparison.OrdinalIgnoreCase));
        var action = item is null ? "API key added" : "API key updated";
        if (item is null)
        {
            item = new ApiCredential { ApiCredentialId = data.NextApiCredentialId++, ServiceName = "remove.bg" };
            data.ApiCredentials.Add(item);
        }
        var protector = protectionProvider.CreateProtector("Pharma40.ApiCredentials.v1");
        item.EncryptedApiKey = protector.Protect(payload.ApiKey.Trim());
        item.MaskedApiKey = MaskApiKey(payload.ApiKey.Trim());
        item.Status = "Active";
        item.LastUpdatedAtUtc = DateTime.UtcNow;
        item.UpdatedBy = current.Email;
        data.AuditTrail.Add(AuditRow(data, "API", item.ServiceName, action, current.Email, context, "Masked API key", item.MaskedApiKey, "API key stored encrypted at rest."));
        store.Write(data);
        return Results.Json(new { success = true, item = new { item.ServiceName, item.MaskedApiKey, item.Status, item.LastUpdatedAtUtc, item.UpdatedBy } });
    }).RequireAuthorization("AdminOnly");

    users.MapPost("/api-management/{service}/status", async (string service, HttpRequest request, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<ApiStatusRequest>() ?? new ApiStatusRequest();
        var data = store.Read();
        var item = data.ApiCredentials.FirstOrDefault(row => row.ServiceName.Equals(service, StringComparison.OrdinalIgnoreCase));
        if (item is null) return Results.NotFound(new { success = false, message = "API service not found." });
        var old = item.Status;
        item.Status = payload.IsActive ? "Active" : "Disabled";
        item.LastUpdatedAtUtc = DateTime.UtcNow;
        item.UpdatedBy = current.Email;
        data.AuditTrail.Add(AuditRow(data, "API", item.ServiceName, payload.IsActive ? "API key reactivated" : "API key disabled", current.Email, context, old, item.Status, ""));
        store.Write(data);
        return Results.Json(new { success = true, item = new { item.ServiceName, item.MaskedApiKey, item.Status, item.LastUpdatedAtUtc, item.UpdatedBy } });
    }).RequireAuthorization("AdminOnly");

    users.MapPost("/api-management/{service}/test", (string service, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var data = store.Read();
        var active = data.ApiCredentials.Any(row => row.ServiceName.Equals(service, StringComparison.OrdinalIgnoreCase) && row.Status.Equals("Active", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.EncryptedApiKey));
        data.AuditTrail.Add(AuditRow(data, "API", service, "API connection tested", current.Email, context, "", active ? "Configured" : "Not configured", active ? "API key is configured. Live external call is skipped from the admin test." : "API key is not active."));
        store.Write(data);
        return Results.Json(new { success = active, message = active ? "API key is configured." : "API key is not configured or inactive." });
    }).RequireAuthorization("AdminOnly");
}

static void MapAuthApi(WebApplication app)
{
    var auth = app.MapGroup("/api/auth");

    auth.MapPost("/login", async (HttpRequest request, AuthService authService, IJwtTokenService jwtTokenService, HttpContext context) =>
    {
        var payload = await request.ReadFromJsonAsync<LoginRequest>() ?? new LoginRequest();
        var result = authService.Login(payload.Email, payload.Password, context);
        if (!result.Success || result.User is null)
        {
            return Results.Json(new { success = false, result.Message }, statusCode: 401);
        }

        context.Session.SetInt32("UserId", result.User.Id);
        context.Session.SetString("FullName", result.User.FullName);
        context.Session.SetString("Email", result.User.Email);
        context.Session.SetString("Role", result.User.Role);
        var token = jwtTokenService.GenerateAccessToken(result.User);
        var refresh = context.RequestServices.GetRequiredService<RefreshTokenService>().Issue(result.User.Id, IpAddress(context));
        SetRefreshTokenCookie(context, refresh.PlainToken);
        authService.RecordAudit("User", result.User.Id.ToString(), "JWT issued", result.User.Email, context, "", token.ExpiresAtUtc.ToString("O"), "");
        authService.RecordAudit("User", result.User.Id.ToString(), "Refresh token issued", result.User.Email, context, "", refresh.Record.ExpiresAtUtc.ToString("O"), "");
        return Results.Json(new
        {
            success = true,
            result.Message,
            accessToken = token.AccessToken,
            tokenType = token.TokenType,
            expiresAtUtc = token.ExpiresAtUtc,
            expiresAtGmt8 = token.ExpiresAtGmt8,
            mustChangePassword = token.MustChangePassword,
            redirectUrl = token.MustChangePassword ? "/Auth?changePassword=1" : "/Dashboard",
            user = new
            {
                token.UserId,
                token.FullName,
                token.Email,
                token.Role,
                token.Department,
                token.Position,
                token.MustChangePassword
            }
        });
    });

    auth.MapPost("/refresh-token", (RefreshTokenService refreshTokenService, AuthService authService, HttpContext context) =>
    {
        var token = context.Request.Cookies["refreshToken"];
        var result = refreshTokenService.Rotate(token ?? "", IpAddress(context));
        if (!result.Success || result.AccessToken is null || result.RefreshToken is null || result.User is null)
        {
            authService.RecordAudit("User", "", "Refresh token failed", "Unknown", context, "", "", result.Message);
            return Results.Json(new { success = false, result.Message }, statusCode: 401);
        }

        SetRefreshTokenCookie(context, result.RefreshToken);
        authService.RecordAudit("User", result.User.Id.ToString(), "Refresh token rotated", result.User.Email, context, "", result.AccessToken.ExpiresAtUtc.ToString("O"), "");
        return Results.Json(new
        {
            success = true,
            message = "Token refreshed.",
            accessToken = result.AccessToken.AccessToken,
            tokenType = result.AccessToken.TokenType,
            expiresAtUtc = result.AccessToken.ExpiresAtUtc,
            expiresAtGmt8 = result.AccessToken.ExpiresAtGmt8,
            user = new
            {
                result.AccessToken.UserId,
                result.AccessToken.FullName,
                result.AccessToken.Email,
                result.AccessToken.Role,
                result.AccessToken.Department,
                result.AccessToken.Position,
                result.AccessToken.MustChangePassword
            }
        });
    });

    auth.MapPost("/revoke-token", async (HttpRequest request, RefreshTokenService refreshTokenService, AuthService authService, HttpContext context) =>
    {
        var payload = await request.ReadFromJsonAsync<RevokeTokenRequest>() ?? new RevokeTokenRequest();
        var token = string.IsNullOrWhiteSpace(payload.Token) ? context.Request.Cookies["refreshToken"] : payload.Token;
        if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest(new { success = false, message = "Token is required." });
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (!user.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase) && !refreshTokenService.UserOwnsToken(user.Id, token))
        {
            authService.RecordAudit("User", user.Id.ToString(), "Forbidden access attempt", user.Email, context, "", "", "Attempted to revoke a refresh token not owned by the user.");
            return Results.Forbid();
        }

        var result = refreshTokenService.Revoke(token, IpAddress(context));
        if (result.Success)
        {
            context.Response.Cookies.Delete("refreshToken");
            authService.RecordAudit("User", user.Id.ToString(), "Refresh token revoked", user.Email, context, "", "", "");
        }
        return Results.Json(new { success = result.Success, result.Message }, statusCode: result.Success ? 200 : 400);
    }).RequireAuthorization();

    auth.MapPost("/register", async (HttpRequest request, AuthService authService, TotpService totpService, HttpContext context) =>
    {
        var payload = await request.ReadFromJsonAsync<RegisterRequest>() ?? new RegisterRequest();
        var result = authService.Register(payload.FullName, payload.Email, payload.Department, payload.Position, payload.Password, payload.EnableAuthenticator, context);
        if (!result.Success || result.User is null || !payload.EnableAuthenticator)
        {
            return Results.Json(new { success = result.Success, result.Message }, statusCode: result.Success ? 200 : 400);
        }

        var setup = totpService.BeginSetup(result.User.Id, context);
        return Results.Json(new
        {
            success = setup.Success,
            message = setup.Success ? "Account created. Set up your authenticator to activate your account." : setup.Message,
            requiresAuthenticatorSetup = setup.Success,
            email = result.User.Email,
            qrCodeSvgDataUri = setup.QrCodeSvgDataUri
        }, statusCode: setup.Success ? 200 : 400);
    });

    auth.MapPost("/forgot-password/reset-with-authenticator", async (HttpRequest request, AuthService authService, TotpService totpService, HttpContext context) =>
    {
        var payload = await request.ReadFromJsonAsync<ResetPasswordRequest>() ?? new ResetPasswordRequest();
        var user = authService.FindByEmail(payload.Email);
        if (user is null || !user.TotpEnabled)
        {
            return Results.Json(new { success = false, message = "Authenticator password reset is not enabled for this account. Contact your system administrator." }, statusCode: 400);
        }
        var verification = totpService.VerifyLoginCode(user, payload.Otp, context);
        if (!verification.Success)
        {
            return Results.Json(new { success = false, verification.Message }, statusCode: 400);
        }
        var result = await authService.ResetPasswordAfterAuthenticator(user.Id, payload.NewPassword, context);
        return Results.Json(new { success = result.Success, result.Message }, statusCode: result.Success ? 200 : 400);
    });

    auth.MapPost("/change-temporary-password", async (HttpRequest request, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<ChangePasswordRequest>() ?? new ChangePasswordRequest();
        var result = await authService.ChangeTemporaryPassword(user.Id, payload.CurrentPassword, payload.NewPassword, payload.ConfirmPassword, context);
        return Results.Json(new { success = result.Success, result.Message, redirectUrl = "/Dashboard" }, statusCode: result.Success ? 200 : 400);
    }).RequireAuthorization();

    auth.MapPost("/totp/setup", (AuthService authService, TotpService totpService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var result = totpService.BeginSetup(user.Id, context);
        return Results.Json(new
        {
            success = result.Success,
            result.Message,
            qrCodeSvgDataUri = result.QrCodeSvgDataUri
        }, statusCode: result.Success ? 200 : 400);
    }).RequireAuthorization();

    auth.MapPost("/totp/verify-setup", async (HttpRequest request, AuthService authService, TotpService totpService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<TotpVerifyRequest>() ?? new TotpVerifyRequest();
        var result = totpService.VerifySetup(user.Id, payload.Code, context);
        return Results.Json(new { success = result.Success, result.Message }, statusCode: result.Success ? 200 : 400);
    }).RequireAuthorization();

    auth.MapPost("/totp/verify-registration", async (HttpRequest request, AuthService authService, TotpService totpService, HttpContext context) =>
    {
        var payload = await request.ReadFromJsonAsync<OtpRequest>() ?? new OtpRequest();
        var user = authService.FindByEmail(payload.Email);
        if (user is null) return Results.NotFound(new { success = false, message = "User not found." });
        var verification = totpService.VerifySetup(user.Id, payload.Otp, context);
        if (!verification.Success) return Results.Json(new { success = false, verification.Message }, statusCode: 400);
        var activation = authService.ConfirmAccountByAuthenticator(payload.Email, context);
        return Results.Json(new { success = activation.Success, activation.Message }, statusCode: activation.Success ? 200 : 400);
    });

    auth.MapGet("/me", (AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        return Results.Json(new
        {
            success = true,
            user = new
            {
                userId = user.Id,
                user.FullName,
                user.Email,
                user.Role,
                user.Department,
                user.Position,
                user.Status,
                user.MustChangePassword,
                user.TotpEnabled
            }
        });
    }).RequireAuthorization();

    var admin = app.MapGroup("/api/admin/email").RequireAuthorization("AdminOnly");
    async Task<IResult> TestEmail(HttpRequest request, AuthService authService, IEmailService emailService, JsonDataStore store, HttpContext context)
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (!user.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<TestSmtpRequest>() ?? new TestSmtpRequest();
        var to = string.IsNullOrWhiteSpace(payload.To) ? user.Email : payload.To;
        var result = await emailService.SendAsync(new EmailMessage
        {
            To = [to],
            RecipientUserId = user.Id,
            Subject = "Pharma 4.0 Email Provider Test",
            TemplateName = "EmailProviderTest",
            RelatedEntityName = "EmailProvider",
            RelatedEntityId = "Test",
            PlainTextBody = "This is a test email from Pharma 4.0 Validation Management using the configured email provider.",
            HtmlBody = "<p>This is a test email from <strong>Pharma 4.0 Validation Management</strong> using the configured email provider.</p>"
        });
        var data = store.Read();
        data.AuditTrail.Add(new AuditTrail
        {
            AuditTrailId = data.NextAuditTrailId++,
            EntityName = "EmailProvider",
            EntityId = "Test",
            Action = result.Success ? $"{result.Provider} test email sent" : $"{result.Provider} test email failed",
            PerformedBy = user.Email,
            PerformedAt = DateTime.UtcNow,
            IPAddress = context.Connection.RemoteIpAddress?.ToString() ?? "",
            DeviceInfo = context.Request.Headers.UserAgent.ToString(),
            NewValue = to,
            Remarks = result.Success ? "Admin SMTP diagnostic succeeded." : result.SafeDiagnosticMessage
        });
        store.Write(data);
        return Results.Json(new
        {
            success = result.Success,
            provider = result.Provider,
            messageId = result.MessageId,
            message = result.Success ? "Email provider test email sent." : result.SafeDiagnosticMessage
        }, statusCode: result.Success ? 200 : 400);
    }

    admin.MapPost("/test", TestEmail);
    admin.MapPost("/test-smtp", TestEmail);
}

static void MapNotificationApi(WebApplication app)
{
    var notifications = app.MapGroup("/api/notifications").RequireAuthorization();

    notifications.MapGet("/", (AuthService authService, JsonDataStore store, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var items = store.Read().Notifications
            .Where(item => item.UserId == user.Id || item.RecipientUserId.Equals(user.UserCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .Take(50);
        return Results.Json(new { success = true, items });
    });

    notifications.MapPost("/{id:int}/read", (int id, AuthService authService, JsonDataStore store, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        var data = store.Read();
        var notification = data.Notifications.FirstOrDefault(item => item.NotificationId == id && (item.UserId == user.Id || item.RecipientUserId == user.UserCode));
        if (notification is null) return Results.NotFound(new { success = false, message = "Notification not found." });
        notification.IsRead = true;
        store.Write(data);
        return Results.Json(new { success = true });
    });
}

static void MapVrmsApi(WebApplication app)
{
    var users = app.MapGroup("/api/users").RequireAuthorization();
    users.MapGet("/active", (AuthService authService, HttpContext context) =>
    {
        if (CurrentUser(authService, context) is null) return Results.Unauthorized();
        var items = authService.ActiveUsers().Select(user => new
        {
            userId = user.Id,
            user.UserCode,
            user.FullName,
            user.Email,
            user.Department,
            user.Position,
            displayText = VrmsRoutingService.FormatUserDisplay(user)
        });
        return Results.Json(new { success = true, items });
    });

    users.MapGet("/by-email/{email}", (string email, AuthService authService, HttpContext context) =>
    {
        if (CurrentUser(authService, context) is null) return Results.Unauthorized();
        var user = authService.FindByEmail(email);
        return user is null ? Results.NotFound(new { success = false, message = "User not found." }) : Results.Json(new { success = true, user });
    });

    var vrms = app.MapGroup("/api/vrms").RequireAuthorization();
    vrms.MapPost("/{id}/route", async (string id, HttpRequest request, VrmsRoutingService routingService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (user.MustChangePassword) return Results.StatusCode(423);
        var payload = await request.ReadFromJsonAsync<VrmsRouteRequest>() ?? new VrmsRouteRequest();
        payload.RoutingTrackerNo = string.IsNullOrWhiteSpace(payload.RoutingTrackerNo) ? id : payload.RoutingTrackerNo;
        var result = routingService.UpsertRoute(payload, user, context);
        return Results.Json(new { success = result.Success, result.Message, record = result.Record }, statusCode: result.Success ? 200 : 400);
    });

    vrms.MapPost("/{id}/send-follow-up", async (string id, HttpRequest request, VrmsRoutingService routingService, AuthService authService, HttpContext context) =>
    {
        var user = CurrentUser(authService, context);
        if (user is null) return Results.Unauthorized();
        if (user.MustChangePassword) return Results.StatusCode(423);
        var payload = await request.ReadFromJsonAsync<VrmsFollowUpRequest>() ?? new VrmsFollowUpRequest();
        var result = routingService.SendFollowUp(id, payload.Remarks, user, context);
        return Results.Json(new { success = result.Success, result.Message, record = result.Record }, statusCode: result.Success ? 200 : 400);
    });

    vrms.MapGet("/{id}/email-logs", (string id, VrmsRoutingService routingService, AuthService authService, HttpContext context) =>
        CurrentUser(authService, context) is null ? Results.Unauthorized() : Results.Json(new { success = true, items = routingService.EmailLogs(id) }));
    vrms.MapGet("/{id}/notifications", (string id, VrmsRoutingService routingService, AuthService authService, HttpContext context) =>
        CurrentUser(authService, context) is null ? Results.Unauthorized() : Results.Json(new { success = true, items = routingService.Notifications(id) }));
    vrms.MapGet("/{id}/audit-trail", (string id, VrmsRoutingService routingService, AuthService authService, HttpContext context) =>
        CurrentUser(authService, context) is null ? Results.Unauthorized() : Results.Json(new { success = true, items = routingService.AuditTrail(id) }));

    app.MapGet("/api/audit-trail", (AuthService authService, JsonDataStore store, HttpContext context) =>
        CurrentUser(authService, context) is null ? Results.Unauthorized() : Results.Json(new { success = true, items = store.Read().AuditTrail.OrderByDescending(item => item.PerformedAt).Take(250) }))
        .RequireAuthorization();
}

static void MapAccountManagementApi(WebApplication app)
{
    var accounts = app.MapGroup("/api/accounts").RequireAuthorization();

    accounts.MapGet("/", (JsonDataStore store) =>
    {
        var data = store.Read();
        return Results.Json(new { success = true, items = data.Users.OrderBy(item => item.FullName).Select(AccountDto) });
    }).RequireAuthorization("AdminOnly");

    accounts.MapGet("/{id:int}", (int id, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (current.Id != id && !current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var user = authService.GetUser(id);
        return user is null ? Results.NotFound(new { success = false, message = "Account not found." }) : Results.Json(new { success = true, account = AccountDto(user) });
    });

    accounts.MapPost("/", async (HttpRequest request, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        var payload = await request.ReadFromJsonAsync<AccountSaveRequest>() ?? new AccountSaveRequest();
        var data = store.Read();
        var email = payload.Email.Trim().ToLowerInvariant();
        if (data.Users.Any(item => item.Email.Equals(email, StringComparison.OrdinalIgnoreCase))) return Results.BadRequest(new { success = false, message = "Email already registered." });
        if (string.IsNullOrWhiteSpace(payload.Password) || payload.Password.Length < 8) return Results.BadRequest(new { success = false, message = "Password must be at least 8 characters." });
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = data.NextUserId++,
            UserCode = $"USR-{now.Year}-{data.NextUserId - 1:0000}",
            FullName = payload.FullName.Trim(),
            FirstName = payload.FirstName.Trim(),
            MiddleInitial = payload.MiddleInitial.Trim(),
            MiddleInitialNotApplicable = payload.MiddleInitialNotApplicable,
            LastName = payload.LastName.Trim(),
            EmployeeIdNo = payload.EmployeeIdNo.Trim(),
            Username = email.Split('@')[0],
            Email = email,
            EmailConfirmed = true,
            Department = payload.Department.Trim(),
            Position = payload.Position.Trim(),
            ProfilePhotoDataUrl = payload.ProfilePhotoDataUrl,
            SignatureDataUrl = payload.SignatureDataUrl,
            SignatureBackgroundWarning = payload.SignatureBackgroundWarning,
            Role = NormalizeRole(payload.Role),
            Status = string.IsNullOrWhiteSpace(payload.Status) ? AccountStatuses.Active : payload.Status.Trim(),
            PasswordHash = PasswordHasher.Hash(payload.Password),
            CreatedAt = now,
            UpdatedAt = now
        };
        data.Users.Add(user);
        data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "User added", current.Email, context, "", user.Email, "New users receive no T-code access until Admin assignment."));
        store.Write(data);
        return Results.Json(new { success = true, account = AccountDto(user) });
    }).RequireAuthorization("AdminOnly");

    accounts.MapPut("/{id:int}", async (int id, HttpRequest request, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (current.Id != id && !current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var payload = await request.ReadFromJsonAsync<AccountSaveRequest>() ?? new AccountSaveRequest();
        var data = store.Read();
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return Results.NotFound(new { success = false, message = "Account not found." });
        var oldValue = $"{user.FullName}|{user.Role}|{user.Status}";
        user.FullName = string.IsNullOrWhiteSpace(payload.FullName) ? user.FullName : payload.FullName.Trim();
        user.FirstName = payload.FirstName?.Trim() ?? user.FirstName;
        user.MiddleInitial = payload.MiddleInitial?.Trim() ?? user.MiddleInitial;
        user.MiddleInitialNotApplicable = payload.MiddleInitialNotApplicable;
        user.LastName = payload.LastName?.Trim() ?? user.LastName;
        user.EmployeeIdNo = payload.EmployeeIdNo?.Trim() ?? user.EmployeeIdNo;
        user.Department = payload.Department?.Trim() ?? user.Department;
        user.Position = payload.Position?.Trim() ?? user.Position;
        user.ProfilePhotoDataUrl = payload.ProfilePhotoDataUrl ?? user.ProfilePhotoDataUrl;
        user.SignatureDataUrl = payload.SignatureDataUrl ?? user.SignatureDataUrl;
        user.SignatureBackgroundWarning = payload.SignatureBackgroundWarning;
        if (current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(payload.Role)) user.Role = NormalizeRole(payload.Role);
            if (!string.IsNullOrWhiteSpace(payload.Status)) user.Status = payload.Status.Trim();
            user.MustChangePassword = payload.MustChangePassword ?? user.MustChangePassword;
        }
        if (!string.IsNullOrWhiteSpace(payload.Password))
        {
            user.PasswordHash = PasswordHasher.Hash(payload.Password);
            user.MustChangePassword = payload.MustChangePassword ?? true;
        }
        user.UpdatedAt = DateTime.UtcNow;
        data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "User profile edited", current.Email, context, oldValue, $"{user.FullName}|{user.Role}|{user.Status}", ""));
        store.Write(data);
        return Results.Json(new { success = true, account = AccountDto(user) });
    });

    accounts.MapPost("/{id:int}/reset-password", (int id, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var data = store.Read();
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return Results.NotFound(new { success = false, message = "Account not found." });

        var now = DateTime.UtcNow;
        data.PasswordHistory.Add(new PasswordHistory { PasswordHistoryId = data.NextPasswordHistoryId++, UserId = user.Id, PasswordHash = user.PasswordHash, CreatedAt = now });
        user.PasswordHash = PasswordHasher.Hash("Welcome@01");
        user.MustChangePassword = true;
        user.Status = AccountStatuses.PendingPasswordChange;
        user.TemporaryPasswordExpiresAt = null;
        user.UpdatedAt = now;
        data.AuditTrail.Add(AuditRow(
            data,
            "User",
            user.Id.ToString(),
            "Password reset to Welcome@01",
            current.Email,
            context,
            "Existing password hash",
            "Default temporary password applied; password change required",
            $"Affected user: {user.Email}. Reset completed by administrator."));
        store.Write(data);
        return Results.Json(new
        {
            success = true,
            message = "Password reset completed. The account must change the temporary password on next login.",
            userId = user.Id,
            user.Email,
            user.MustChangePassword
        });
    }).RequireAuthorization("AdminOnly");

    accounts.MapPost("/{id:int}/totp/reset", (int id, AuthService authService, TotpService totpService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var result = totpService.ResetAuthenticator(id, current, context);
        return Results.Json(new { success = result.Success, result.Message }, statusCode: result.Success ? 200 : 400);
    }).RequireAuthorization("AdminOnly");

    accounts.MapPost("/{id:int}/reactivate", (int id, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var data = store.Read();
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return Results.NotFound(new { success = false, message = "Account not found." });
        var oldStatus = user.Status;
        user.Status = AccountStatuses.Active;
        user.LockedUntilUtc = null;
        user.UpdatedAt = DateTime.UtcNow;
        data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "User reactivated", current.Email, context, oldStatus, user.Status, ""));
        store.Write(data);
        return Results.Json(new { success = true, message = "Account reactivated.", account = AccountDto(user) });
    }).RequireAuthorization("AdminOnly");

    accounts.MapPost("/{id:int}/unlock", (int id, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (!current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var data = store.Read();
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return Results.NotFound(new { success = false, message = "Account not found." });
        var oldValue = $"{user.Status}|{user.FailedLoginAttempts}|{user.LockedUntilUtc:O}";
        user.Status = AccountStatuses.Active;
        user.FailedLoginAttempts = 0;
        user.LockedUntilUtc = null;
        user.UpdatedAt = DateTime.UtcNow;
        data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "Account unlocked", current.Email, context, oldValue, $"{user.Status}|0|", ""));
        store.Write(data);
        return Results.Json(new { success = true, message = "Account unlocked.", account = AccountDto(user) });
    }).RequireAuthorization("AdminOnly");

    accounts.MapDelete("/{id:int}", (int id, JsonDataStore store, AuthService authService, HttpContext context) =>
    {
        var current = CurrentUser(authService, context);
        if (current is null) return Results.Unauthorized();
        if (current.Id != id && !current.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
        var data = store.Read();
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return Results.NotFound(new { success = false, message = "Account not found." });
        var oldStatus = user.Status;
        user.Status = AccountStatuses.Deactivated;
        user.UpdatedAt = DateTime.UtcNow;
        data.AuditTrail.Add(AuditRow(data, "User", user.Id.ToString(), "User disabled", current.Email, context, oldStatus, user.Status, "Soft deactivation; no hard delete."));
        store.Write(data);
        return Results.Json(new { success = true, message = "Account deactivated." });
    });
}

static Pharma40.Models.User? CurrentUser(AuthService authService, HttpContext context)
{
    var userId = context.Session.GetInt32("UserId");
    if (userId is not null) return authService.GetUser(userId.Value);
    var sub = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
    return int.TryParse(sub, out var jwtUserId) ? authService.GetUser(jwtUserId) : null;
}

static void SetRefreshTokenCookie(HttpContext context, string token)
{
    context.Response.Cookies.Append("refreshToken", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Expires = DateTimeOffset.UtcNow.AddDays(7)
    });
}

static string IpAddress(HttpContext context)
{
    if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) && !string.IsNullOrWhiteSpace(forwarded))
    {
        return forwarded.ToString().Split(',')[0].Trim();
    }

    return context.Connection.RemoteIpAddress?.ToString() ?? "";
}

static async Task<(bool Success, string Message, string DataUrl)> ReadImageDataUrl(IFormFile file, string[] allowedContentTypes, long maxBytes)
{
    if (!allowedContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
    {
        return (false, "Invalid image file type.", "");
    }
    if (file.Length > maxBytes)
    {
        return (false, "Image file size exceeds the allowed limit.", "");
    }
    await using var stream = file.OpenReadStream();
    using var memory = new MemoryStream();
    await stream.CopyToAsync(memory);
    return (true, "", $"data:{file.ContentType};base64,{Convert.ToBase64String(memory.ToArray())}");
}

static bool PngHasTransparency(byte[] bytes)
{
    if (bytes.Length < 33 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47) return false;
    var colorType = bytes[25];
    if (colorType is 4 or 6) return true;
    for (var i = 8; i < bytes.Length - 12; i++)
    {
        if (bytes[i] == (byte)'t' && bytes[i + 1] == (byte)'R' && bytes[i + 2] == (byte)'N' && bytes[i + 3] == (byte)'S') return true;
    }
    return false;
}

static async Task<(bool Success, string Message, byte[] Bytes)> RemoveSignatureBackground(byte[] pngBytes, JsonDataStore store, IHttpClientFactory httpClientFactory, IDataProtectionProvider protectionProvider, IConfiguration configuration, string performedBy, HttpContext context, AppData data)
{
    var apiKey = ResolveRemoveBgApiKey(data, protectionProvider, configuration);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return (false, "remove.bg API key is not configured. Please contact the Administrator or upload a transparent PNG signature.", []);
    }

    try
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(pngBytes), "image_file", "signature.png");
        content.Add(new StringContent("auto"), "size");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.remove.bg/v1.0/removebg");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = content;
        using var response = await httpClientFactory.CreateClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            data.AuditTrail.Add(AuditRow(data, "API", "remove.bg", "remove.bg processing failure", performedBy, context, "", response.StatusCode.ToString(), "Unable to remove background automatically. Please upload a transparent PNG signature."));
            return (false, "Unable to remove background automatically. Please upload a transparent PNG signature.", []);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (!PngHasTransparency(bytes))
        {
            data.AuditTrail.Add(AuditRow(data, "API", "remove.bg", "remove.bg processing failure", performedBy, context, "", "No transparency detected", "Unable to remove background automatically. Please upload a transparent PNG signature."));
            return (false, "Unable to remove background automatically. Please upload a transparent PNG signature.", []);
        }

        data.AuditTrail.Add(AuditRow(data, "API", "remove.bg", "remove.bg processing success", performedBy, context, "Visible background", "Transparent PNG", "Signature background removed successfully."));
        return (true, "Signature background removed successfully.", bytes);
    }
    catch
    {
        data.AuditTrail.Add(AuditRow(data, "API", "remove.bg", "remove.bg processing failure", performedBy, context, "", "Exception", "Unable to remove background automatically. Please upload a transparent PNG signature."));
        return (false, "Unable to remove background automatically. Please upload a transparent PNG signature.", []);
    }
}

static string ResolveRemoveBgApiKey(AppData data, IDataProtectionProvider protectionProvider, IConfiguration configuration)
{
    var configured = configuration["REMOVE_BG_API_KEY"] ?? Environment.GetEnvironmentVariable("REMOVE_BG_API_KEY");
    if (!string.IsNullOrWhiteSpace(configured)) return configured;
    var credential = data.ApiCredentials.FirstOrDefault(item => item.ServiceName.Equals("remove.bg", StringComparison.OrdinalIgnoreCase) && item.Status.Equals("Active", StringComparison.OrdinalIgnoreCase));
    if (credential is null || string.IsNullOrWhiteSpace(credential.EncryptedApiKey)) return "";
    try
    {
        return protectionProvider.CreateProtector("Pharma40.ApiCredentials.v1").Unprotect(credential.EncryptedApiKey);
    }
    catch
    {
        return "";
    }
}

static string MaskApiKey(string key)
{
    if (key.Length <= 8) return "********";
    return $"{key[..Math.Min(7, key.Length)]}************{key[^4..]}";
}

static void EnsureApiCredentialSeed(AppData data)
{
    if (data.ApiCredentials.Any(item => item.ServiceName.Equals("remove.bg", StringComparison.OrdinalIgnoreCase))) return;
    data.ApiCredentials.Add(new ApiCredential
    {
        ApiCredentialId = data.NextApiCredentialId++,
        ServiceName = "remove.bg",
        Status = "Inactive",
        MaskedApiKey = "",
        UpdatedBy = "System"
    });
}

static object AccountDto(User user) => new
{
    userId = user.Id,
    user.UserCode,
    user.FullName,
    user.Email,
    user.FirstName,
    user.MiddleInitial,
    user.MiddleInitialNotApplicable,
    user.LastName,
    user.EmployeeIdNo,
    user.Department,
    user.Position,
    user.PhotoIdDataUrl,
    user.ProfilePhotoDataUrl,
    user.SignatureDataUrl,
    user.SignatureBackgroundWarning,
    user.Role,
    user.Status,
    user.MustChangePassword,
    user.TotpEnabled,
    user.FailedLoginAttempts,
    user.LastLoginAtUtc,
    user.PasswordExpiresAtUtc,
    user.LockedUntilUtc,
    user.LastPasswordChangedAtUtc,
    user.CreatedAtUtc,
    user.UpdatedAtUtc
};

static string NormalizeRole(string role)
{
    if (role.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return "Administrator";
    if (role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return "Administrator";
    return "User";
}

static string NormalizeTCodeAccess(string accessLevel)
{
    if (accessLevel.Equals("Full Access", StringComparison.OrdinalIgnoreCase)) return "Full Access";
    if (accessLevel.Equals("View Only", StringComparison.OrdinalIgnoreCase)
        || accessLevel.Equals("View Only Access", StringComparison.OrdinalIgnoreCase)
        || accessLevel.Equals("Search / Read Only", StringComparison.OrdinalIgnoreCase))
    {
        return "View Only Access";
    }

    return "No Access";
}

static AuditTrail AuditRow(AppData data, string entityName, string entityId, string action, string performedBy, HttpContext context, string oldValue, string newValue, string remarks) => new()
{
    AuditTrailId = data.NextAuditTrailId++,
    EntityName = entityName,
    EntityId = entityId,
    Action = action,
    PerformedBy = performedBy,
    PerformedAt = DateTime.UtcNow,
    IPAddress = context.Connection.RemoteIpAddress?.ToString() ?? "",
    DeviceInfo = context.Request.Headers.UserAgent.ToString(),
    OldValue = oldValue,
    NewValue = newValue,
    Remarks = remarks
};

public sealed class RegisterRequest
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Department { get; set; } = "";
    public string Position { get; set; } = "";
    public string Password { get; set; } = "";
    public bool EnableAuthenticator { get; set; }
}

public sealed class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class OtpRequest
{
    public string Email { get; set; } = "";
    public string Otp { get; set; } = "";
}

public sealed class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";
}

public sealed class ResetPasswordRequest
{
    public string Email { get; set; } = "";
    public string Otp { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public sealed class TestSmtpRequest
{
    public string To { get; set; } = "";
}

public sealed class RevokeTokenRequest
{
    public string Token { get; set; } = "";
}

public sealed class TotpVerifyRequest
{
    public string Code { get; set; } = "";
}

public sealed class AccountSaveRequest
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string MiddleInitial { get; set; } = "";
    public bool MiddleInitialNotApplicable { get; set; }
    public string LastName { get; set; } = "";
    public string EmployeeIdNo { get; set; } = "";
    public string Department { get; set; } = "";
    public string Position { get; set; } = "";
    public string ProfilePhotoDataUrl { get; set; } = "";
    public string SignatureDataUrl { get; set; } = "";
    public bool SignatureBackgroundWarning { get; set; }
    public string Role { get; set; } = "User";
    public string Status { get; set; } = "";
    public bool? MustChangePassword { get; set; }
}

public sealed class TCodeAccessSaveRequest
{
    public int UserId { get; set; }
    public string TCode { get; set; } = "";
    public string AccessLevel { get; set; } = "No Access";
    public bool IsActive { get; set; } = true;
    public string Remarks { get; set; } = "";
}

public sealed class ApiCredentialSaveRequest
{
    public string ServiceName { get; set; } = "remove.bg";
    public string ApiKey { get; set; } = "";
}

public sealed class ApiStatusRequest
{
    public bool IsActive { get; set; }
}

public sealed class ArchiveMaintenanceService(IServiceProvider services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var next = new DateTime(now.Year, 12, 31, 0, 0, 0);
            if (now >= next) next = new DateTime(now.Year + 1, 12, 31, 0, 0, 0);
            try
            {
                await Task.Delay(next - now, stoppingToken);
                using var scope = services.CreateScope();
                var archiveService = scope.ServiceProvider.GetRequiredService<ArchiveService>();
                archiveService.RunAnnualArchive("System", "Annual archive trigger: December 31 12:00 midnight", null);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
    }
}
