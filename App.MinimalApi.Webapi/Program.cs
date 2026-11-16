var builder = WebApplication.CreateBuilder(args);

// Structured logging (DEFECT FIX: Serilog packages were referenced but never configured, so
// LogContext properties were silently dropped).
builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "App.MinimalApi.Webapi")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 1_048_576; // global ceiling; the proxy applies its own lower limits
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddSingleton<AspnetcoreHttpcontextEnricher>();
builder.Services.AddProxySecurity(builder.Configuration);
builder.Services.AddProxyRateLimiting(builder.Configuration);
builder.Services.AddServices(builder.Configuration);

builder.Services.AddSwaggerGen(c =>
{
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlCommentsFullPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlCommentsFullPath))
    {
        c.IncludeXmlComments(xmlCommentsFullPath);
    }

    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Version = "v1",
        Title = "App.MinimalApi.Webapi - Secure Proxy",
        Description = "Configuration-driven, fail-closed proxy API"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header
    });
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, Name = ProxyHeaders.ApiKey, In = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = []
    });
});

builder.Services.AddCarter();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(); // method, path, status, elapsed - no headers, no bodies
app.UseExceptionHandler();      // DEFECT FIX: activates GlobalExceptionHandler

// DEFECT FIX: Swagger (full API surface) was exposed in every environment.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "App.MinimalApi.Webapi v1"));
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapCarter();

app.Run();

/// <summary>Entry point marker for WebApplicationFactory based tests.</summary>
public partial class Program;
