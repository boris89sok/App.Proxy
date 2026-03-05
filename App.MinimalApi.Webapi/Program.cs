var builder = WebApplication.CreateBuilder(args);
var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpContextAccessor();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddSingleton<AspnetcoreHttpcontextEnricher>();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddServices(builder.Configuration);

builder.Services.AddApiVersioning(opt =>
{
    opt.DefaultApiVersion = new ApiVersion(1, 0);
    opt.AssumeDefaultVersionWhenUnspecified = true;
    opt.ReportApiVersions = true;
    opt.ApiVersionReader = new UrlSegmentApiVersionReader();
});

builder.Services.AddSwaggerGen(c =>
{
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlCommentsFullPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlCommentsFullPath))
    {
        c.IncludeXmlComments(xmlCommentsFullPath);
    }

    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Version = "v1",
        Title = "App.MinimalApi.Webapi",
        Description = "A simple example ASP.NET Core Web API"
    });
});

builder.Services.AddCarter();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{

}

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "App.MinimalApi.Webapi v1"));
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/api/healthcheck");
app.MapCarter();

app.Run();