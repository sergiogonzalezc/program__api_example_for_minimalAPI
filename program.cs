using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Carter;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NLog;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using FluentValidation;
using Microsoft.OpenApi.Models;
using Asp.Versioning.ApiExplorer;
using Asp.Versioning;
using Microsoft.EntityFrameworkCore.Migrations;
using FluentAssertions.Common;
using TopTravel.Application.AWS;
using Polly;
using Polly.Retry;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http.Features;


var builder = WebApplication.CreateBuilder(args);


// Add services to the container.

string envName = builder.Environment.EnvironmentName;

if (envName != "Development" && envName != "qa")
{
    builder.Services.AddTransient<ProblemDetailsFactory, CustomProblemDetailsFactory>();
}

// Add services to the container.

builder.Services.AddScoped<IProviderApplication, ProviderApplication>();
builder.Services.AddScoped<IProviderRepository, ProviderRepository>();
builder.Services.AddScoped(typeof(IProviderRepository), typeof(ProviderRepository));

builder.Services.AddScoped<IUserApplication, UserApplication>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped(typeof(IUserRepository), typeof(UserRepository));

builder.Services.AddScoped<IAuthApplication, AuthApplication>();
builder.Services.AddScoped(typeof(IAuthApplication), typeof(AuthApplication));

builder.Services.AddScoped<S3Service>();

#region ===================== RESILIENCIA POLLY =====================================

builder.Services.AddHttpClient<ProvidersEndpoint>().AddStandardResilienceHandler();
builder.Services.AddSingleton<ProvidersEndpoint>();

builder.Services.AddHttpClient<UsersEndpoint>().AddStandardResilienceHandler();
builder.Services.AddSingleton<UsersEndpoint>();

builder.Services.AddResiliencePipeline("default", x =>
{
    x.AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<Exception>(),
        Delay = TimeSpan.FromSeconds(10),
        MaxRetryAttempts = 2,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true
    })
        .AddTimeout(TimeSpan.FromSeconds(30));
});

#endregion ===================== FIN RESILIENCIA POLLY =====================================

TopTravel.Application.AuthConfiguration.Jwt.JwtConfiguration = builder.Configuration.GetSection("Jwt").Get<JwtConfiguration>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("localhost", builder =>
    {
        builder.WithOrigins("http://localhost:3000",
                            "http://localhost:3001",
                            "http://localhost:80"
                            )
                            .AllowAnyMethod()
                            .AllowAnyHeader()
                            .AllowCredentials();
        //.WithExposedHeaders("content-disposition");
    });
});


builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerDocument();
//builder.Services.AddSwaggerGen();
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ErrorHandlingFilterAttribute>();
});

foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
{
    builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(assembly)
                                        .AddOpenBehavior(typeof(ValidationBehavior<,>))
                                        .AddOpenBehavior(typeof(LoggingBehavior<,>))
    );

    builder.Services.AddValidatorsFromAssembly(assembly);
}

builder.Services.AddCarter();

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

builder.Services.AddOptions();

builder.Services.AddMemoryCache();

//Cross-Cutting Services
builder.Services.AddExceptionHandler<CustomExceptionHandler>();

// Mejora el rendimiento comprimiendo la respuesta a la solicitud http
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

// Mejora el rendimiento implementado una tasa limite máximo de llamados por IP
/*
 * Maybe you want to have a limit where one can make 600 requests per minute, but only 6000 per hour. You could chain two FixedWindowLimiter with different options.
 
- Concurrency limit is the simplest form of rate limiting. It doesn’t look at time, just at number of concurrent requests. “Allow 10 concurrent requests”.
- Fixed window limit lets you apply limits such as “60 requests per minute”. Every minute, 60 requests can be made. One every second, but also 60 in one go.
- Sliding window limit is similar to the fixed window limit, but uses segments for more fine-grained limits. Think “60 requests per minute, with 1 request per second”.
- Token bucket limit lets you control flow rate, and allows for bursts. Think “you are given 100 requests every minute”. If you make all of them over 10 seconds, you’ll have to wait for 1 minute before you are allowed more requests.

options.QueueProcessingOrder => Oldest First (LIFO-Last in first out)
options.QueueProcessingOrder => Newest First(FIFO-First in first out)

  */
builder.Services.AddRateLimiter(_ =>
{
    _.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
            context.HttpContext.Response.WriteAsync($"Demasiadas solicitudes. Inténtalo de nuevo después de {retryAfter.TotalMinutes.ToString(NumberFormatInfo.InvariantInfo)} minutos(s). ");
        }
        else
        {
            context.HttpContext.Response.WriteAsync("Demasiadas solicitudes. Inténtalo de nuevo mas tarde.");
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        return new ValueTask();
    };


    _.AddFixedWindowLimiter("Api", options =>
    {
        options.AutoReplenishment = true;
        options.PermitLimit = 1; // limite maximo de request por la ventana de tiempo
        options.QueueLimit = 0; // puede encolar en vez de rechazar inmediatamente
        options.Window = TimeSpan.FromSeconds(1);
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });

    _.AddFixedWindowLimiter("Web", options =>
    {
        options.AutoReplenishment = true;
        options.PermitLimit = 1;
        options.QueueLimit = 0;
        options.Window = TimeSpan.FromSeconds(1);
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

// Mejora el rendimiento habilitando el protocolo Http2 y http3
//builder.WebHost.ConfigureKestrel(options =>
//{
//    options.ListenAnyIP(5000);  // http/1.1
//    options.ListenAnyIP(5001, listenOptions =>
//    {
//        listenOptions.UseHttps();
//        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2AndHttp3;
//    });
//});



builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("manager_policy", policy => policy.RequireRole("manager")
                     //.RequireClaim("scope", "greetings_api")
                     );
    options.AddPolicy("user_policy", policy => policy.RequireRole("user")
                    //.RequireClaim("scope", "greetings_api")
                    );
});


builder.Services.AddHealthChecks();

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    // Customize other options as needed
});


builder.Services.AddApiVersioning(opt =>
{
    opt.DefaultApiVersion = new ApiVersion(1);
    opt.ReportApiVersions = true;
    opt.AssumeDefaultVersionWhenUnspecified = false;
    opt.ApiVersionReader = new UrlSegmentApiVersionReader();
    //opt.ApiVersionReader = ApiVersionReader.Combine(new QueryStringApiVersionReader("x-api-version"),
    //                                                new HeaderApiVersionReader("x-api-version"));
}).AddApiExplorer(setup =>
{
    setup.GroupNameFormat = "'v'V";
    setup.SubstituteApiVersionInUrl = true;
});

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Version = "v1",
        Title = "API Travel",
        Description = "Api Rest que permite interactuar con los servicios expuestos",
        //Contact = new OpenApiContact
        //{
        //    Name = "Datos de Contacto",
        //    Email = "datos de email"
        //    // Url = new Uri("https://example.com/contact").ToString()
        //}
    });
});

builder.Services.ConfigureOptions<ConfigureSwaggerGenOptions>();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        IReadOnlyList<ApiVersionDescription> descriptionsList = app.DescribeApiVersions();
        foreach (ApiVersionDescription description in descriptionsList)
        {
            string url = $"/swagger/{description.GroupName}/swagger.json";
            string name = description.GroupName.ToUpperInvariant();

            options.SwaggerEndpoint(url, name);
        }
    });

    app.UseDeveloperExceptionPage();
}
else
{
    //Solo habilitado en producción
    //app.UseSwagger();
    //app.UseSwaggerUI(options =>
    //{
    //    IReadOnlyList<ApiVersionDescription> descriptionsList = app.DescribeApiVersions();
    //    foreach (ApiVersionDescription description in descriptionsList)
    //    {
    //        string url = $"/swagger/{description.GroupName}/swagger.json";
    //        string name = description.GroupName.ToUpperInvariant();

    //        options.SwaggerEndpoint(url, name);
    //    }
    //});

    //app.UseHsts();
}

// define culture spanish CL
var cultureInfo = new CultureInfo("es-CL");
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(cultureInfo),
    SupportedCultures = new List<CultureInfo>
                {
                    cultureInfo,
                },
    SupportedUICultures = new List<CultureInfo>
                {
                    cultureInfo,
                }
});

//app.UseMiddleware<ValidationExceptionHandlingMiddleware>();

app.UseRateLimiter();
app.UseCors("localhost");
app.UseAuthentication();
app.UseRouting();
app.UseAuthorization();
app.UseAntiforgery();



// SGC - Asigna la version del Assembly al Log4Net
Assembly thisApp = Assembly.GetExecutingAssembly();

AssemblyName name = new AssemblyName(thisApp.FullName);

// Identifica la versión del ensamblado 
GlobalDiagnosticsContext.Set("VersionApp", name.Version.ToString());

// Identifica el nombre del ensamblado para poder identificar el ejecutable
GlobalDiagnosticsContext.Set("AppName", name.Name);

// Obtiene el process ID
Process currentProcess = Process.GetCurrentProcess();

GlobalDiagnosticsContext.Set("ProcessID", "PID " + currentProcess.Id.ToString());


app.Map("/error", () =>
{
    ServiceLog.Write(TopTravel.Common.Enum.LogType.WebSite, System.Diagnostics.TraceLevel.Error, "INICIO_API", "An Error Occurred...!!");

    throw new InvalidOperationException("An Error Occurred...");
});

app.UseRouting();
app.UseResponseCaching();
app.UseHttpsRedirection();
app.MapControllers();
app.MapCarter();
app.UseExceptionHandler(options => { });

app.UseHealthChecks("/health",
    new HealthCheckOptions
    {
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });


ServiceLog.Write(TopTravel.Common.Enum.LogType.WebSite, System.Diagnostics.TraceLevel.Info, "INICIO_API", $"===== INICIO API en modo [{envName}] .=====");

app.Run();
