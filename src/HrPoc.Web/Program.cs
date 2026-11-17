using System.Text.Json.Serialization;
using HrPoc.Web.Data;
using HrPoc.Web.Middleware;
using HrPoc.Web.Models;
using HrPoc.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

var builder = WebApplication.CreateBuilder(args);

static IEdmModel GetEdmModel()
{
    var modelBuilder = new ODataConventionModelBuilder();
    modelBuilder.Namespace = "MyDemoCorp.HrPoc";
    modelBuilder.ContainerName = "HrContainer";

    modelBuilder.EntitySet<Employee>("Employees");
    modelBuilder.EntitySet<Department>("Departments");
    modelBuilder.EntitySet<CostCenter>("CostCenters");
    modelBuilder.EntitySet<Location>("Locations");

    return modelBuilder.GetEdmModel();
}

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    })
    .AddOData(options =>
    {
        options.Select().Filter().OrderBy().Expand().Count().SetMaxTop(50000)
            .AddRouteComponents("odata", GetEdmModel());
    });

builder.Services.AddHttpClient();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "HrPoc.AuthSession";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

var connString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<HrDbContext>(options =>
{
    options.UseSqlServer(connString, sqlOptions =>
    {
        sqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
    });
});

builder.Services.AddScoped<CsvDataSeeder>();
builder.Services.AddSingleton<OidcAuthService>();

var app = builder.Build();

// Initialize DB & seed from CSV on startup
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var seeder = scope.ServiceProvider.GetRequiredService<CsvDataSeeder>();
        await seeder.EnsureSeededAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization / CSV seeding failed at startup.");
    }
}

// Ensure trailing slash when hosted under an IIS virtual application (e.g., /hr -> /hr/)
app.Use(async (context, next) =>
{
    if (context.Request.PathBase.HasValue &&
        (!context.Request.Path.HasValue || context.Request.Path.Value == string.Empty))
    {
        context.Response.Redirect(context.Request.PathBase.Value + "/" + context.Request.QueryString, permanent: false);
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
    }
});

app.UseRouting();

app.UseAuthentication();
app.UseMiddleware<DynamicAuthMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.Run();
