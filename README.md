**SCAFFOLD**

```
Scaffold-DbContext -Connection "Data Source=(local)\SQLEXPRESS;Initial Catalog=[BD];User Id=sa;Password=[PASS_HERE];TrustServerCertificate=true;Trusted_Connection=true" -Provider "Microsoft.EntityFrameworkCore.SqlServer" -OutputDir "E:\GitHub\PROJECT_NAME\Backend\DAL\Model" -ContextDir "E:\GitHub\PTOJECT_NAME\Backend\DAL" -Namespace PROJECT_NAME.Infrastructure.Model -ContextNamespace PROJECT_NAME.Infrastructure  -Context "DBContextData" -Schemas "dbo" -noPluralize -Force -Project "Demo.DAL"

```

**API VERSIONING**

First, add to api project this nuget packages: 
- Asp.Versioning.Http
- Asp.Versioning.Mvc.ApiExplorer
  
Then, in the program class:

```
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
```

Finallly, using minimal api, use this to apply versioning to complete api set:

```
  public class CountyEndpoint : ICarterModule
  {
      public void AddRoutes(IEndpointRouteBuilder app)
      {
          ApiVersionSet versionSet = app.NewApiVersionSet()
                             .HasApiVersion(new ApiVersion(1))
                             //.HasApiVersion(new ApiVersion(2))
                             //.HasDeprecatedApiVersion
                             .ReportApiVersions()
                             .Build();

          RouteGroupBuilder groupPublic = app
                              .MapGroup("api/v{apiVersion:apiVersion}/public/countys")
                              .WithApiVersionSet(versionSet)
                              .WithSummary("Operaciones sobre ...")
                              .WithOpenApi()
                              .RequireRateLimiting("Api");
          //.AddEndpointFilter<ApiKeyAuthorizationEndpointFilter>();   // filtro de api-key

          #region ======================== APIS PUBLICAS ========================

          groupPublic.MapGet("", GetCountys)
                      .Produces<CountyDTO>(StatusCodes.Status200OK)
                      .ProducesProblem(StatusCodes.Status400BadRequest)
                      .ProducesProblem(StatusCodes.Status404NotFound)
                      .WithSummary("Obtiene lista de...")
                      .WithDescription("Obtiene lista de...");

	}
 }

```

**SWAGGER Example**

```
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Version = "v1",
        Title = "Tiple of the API",
        Description = "Api Rest que permite ....",
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
```

**ConfigureSwaggerGenOptions class**

```
using System;
using Asp.Versioning.ApiExplorer;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace [Namespace]
{
	public class ConfigureSwaggerGenOptions : IConfigureNamedOptions<SwaggerGenOptions>
    {
        private readonly IApiVersionDescriptionProvider _provider;

        public ConfigureSwaggerGenOptions(IApiVersionDescriptionProvider provider)
        {
            _provider = provider;
        }

        public void Configure(SwaggerGenOptions options)
		{
            foreach(ApiVersionDescription description in _provider.ApiVersionDescriptions)
            {
                var openApiInfo = new OpenApiInfo
                {
                    Title = $"RunTrackr.Api v{description.ApiVersion}",
                    Version = description.ApiVersion.ToString()
                };

                options.SwaggerDoc(description.GroupName, openApiInfo);
            }
        }

        public void Configure(string? name, SwaggerGenOptions options)
        {

        }
    }
}

```

**Response Compression**

```
// Improves performance by compressing the response to the http request
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

```

** CORS **

```
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
```

**Error Handler**

***Class 1***
```
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ErrorHandlingFilterAttribute>();
});


  public class ErrorHandlingFilterAttribute : ExceptionFilterAttribute
  {
      private readonly ILogger<ErrorHandlingFilterAttribute> _logger;

      public ErrorHandlingFilterAttribute(ILogger<ErrorHandlingFilterAttribute> logger)
      {
          _logger = logger;
      }

      public override void OnException(ExceptionContext context)
      {
          var exception = context.Exception;

          var problemDetails = new ProblemDetails
          {
                  //context.Exception.Message, // Or a different generic message
                  //context.Exception.Source,
                  //ExceptionType = context.Exception.GetType().FullName,

              Title = "Unhandled error",
              Status = (int)HttpStatusCode.InternalServerError
          };

          // Log the exception
          _logger.LogError("Unhandled exception occurred while executing request: {ex}", context.Exception);

          context.Result = new ObjectResult(problemDetails);

          context.ExceptionHandled = true;

          base.OnException(context);
      }
  }

```

***Class 2 (I prefer use this, because use problemDetails pattern :)

```
builder.Services.AddExceptionHandler<CustomExceptionHandler>();


public class CustomExceptionHandler(ILogger<CustomExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        //logger.LogError("Error Message: {exceptionMessage}, Time of occurrence {time}",            exception.Message, DateTime.UtcNow);
        //ServiceLog.Write(LogType.WebSite, exception, nameof(TryHandleAsync), "Error");

        (string Detail, string Title, int StatusCode) details = exception switch
        {
            InternalServerException =>
            (
                exception.Message,
                exception.GetType().Name,
                context.Response.StatusCode = StatusCodes.Status500InternalServerError
            ),
            ValidationException =>
            (
                exception.Message,
                exception.GetType().Name,
                context.Response.StatusCode = StatusCodes.Status400BadRequest
            ),
            ValidationExceptionAgruped =>
            (
                exception.Message,
                exception.GetType().Name,
                context.Response.StatusCode = StatusCodes.Status400BadRequest
            ),
            CustomValidationException =>
            (
                exception.Message,
                exception.GetType().Name,
                context.Response.StatusCode = StatusCodes.Status400BadRequest
            ),
            BadRequestException =>
            (
                exception.Message,
                exception.GetType().Name,
                context.Response.StatusCode = StatusCodes.Status400BadRequest
            ),
            NotFoundException =>
            (
                exception.Message,
                exception.GetType().Name,
                context.Response.StatusCode = StatusCodes.Status404NotFound
            ),
            _ =>
            (
                exception.Message,
                exception.GetType().Name,
                context.Response.StatusCode = StatusCodes.Status500InternalServerError
            )
        };

        var problemDetails = new ProblemDetailsCustom();

        problemDetails.Title = details.Title;
        problemDetails.Status = details.StatusCode;
        //problemDetails.Detail = details.Detail;
        //problemDetails.Instance = context.Request.Path;
        problemDetails.success = false;

        //problemDetails.Extensions.Add("traceId", context.TraceIdentifier);

        if (exception is ValidationException validationException)
        {
            //problemDetails.Extensions.Add("ValidationErrors", validationException.Errors);

            List<ValidationError> customErrorList = new List<ValidationError>();
            foreach (var error in validationException.Errors)
            {
                ValidationError item = new ValidationError()
                {
                    ErrorCode = error.ErrorCode,
                    ErrorMessage = error.ErrorMessage,
                    PropertyName = error.PropertyName,
                };

                problemDetails.code = item.ErrorCode;
                problemDetails.message = item.ErrorMessage;
                problemDetails.property = item.PropertyName;                
                //customErrorList.Add(item);
                break;
            }

            //problemDetails.Extensions.Add("ValidationErrors", customErrorList);
            //string errorList = string.Join(",", validationException.Errors);            
            ServiceLog.Write(LogType.WebSite, System.Diagnostics.TraceLevel.Error, nameof(TryHandleAsync), $"Error de validación de entrada: status [{problemDetails.Status}] codigoRespuesta [{problemDetails.code}] descripcionRespuesta [{problemDetails.message}]");
        }
        else
        {
            problemDetails.message = exception.Message;
            //problemDetails.Detail = details.Detail;  // agrega el detalle si no es una validacion de Input

            // Logea toda la traza
            ServiceLog.Write(LogType.WebSite, exception, nameof(TryHandleAsync), "Error en el proceso");
        }

        // Agrega agrupación de errores
        if (exception is ValidationExceptionAgruped validationExceptionAgruped)
        {
            problemDetails.Extensions.Add("ValidationErrors", validationExceptionAgruped.Errors);
        }


        await context.Response.WriteAsJsonAsync(problemDetails, cancellationToken: cancellationToken);
        return true;
    }


    public class ProblemDetailsCustom : ProblemDetails
    {
        public bool success { get; set; }
        public string code { get; set; }
        public string message { get; set; }

        public string property { get; set; }
    }


```


**GENERIC BASE MODEL RESPONSE**

```
public class BaseModel
{
	public bool success { get; set; }
	public string message { get; set; }
}


public class GenericBaseModel<T> : BaseModel
{
	public T? Data { get; set; }
	public List<T>? DataList { get; set; }
}

```

Then, you must call this:

```
public class CountryDataResponse : GenericBaseModel<CountryData> { }

```


**My NLog.Config file recomentation (write async log files and split them in separated files each 5 mb)**

First, for troubleshooting in the log files, we recommend create a folder in ***c:\temp\***

```
<?xml version="1.0" encoding="utf-8" ?>
<nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd"
      xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
      autoReload="true"
      throwConfigExceptions="true"
      internalLogLevel="Info"
      internalLogFile="c:\temp\internal-log.txt">

	<extensions>
		<add assembly="NLog.Web.AspNetCore"/>
	</extensions>

	<targets>
		<target name="AsyncServerSite" xsi:type="AsyncWrapper">
			<target xsi:type="File"
					name="ServerSite"
					fileName="${basedir}/Logs/nlog-${shortdate}.log"
					layout="[${uppercase:${level}}] [${longdate}] [${logger}] [${gdc:item=VersionApp}] [${gdc:item=AppName}] [${gdc:item=ProcessID}] ${message} ${exception:format=tostring} url: ${aspnet-request-url} action: ${aspnet-mvc-action}"
					archiveNumbering="Rolling"
					archiveAboveSize="524288"
			  />
		</target>

	</targets>

	<rules>
		<logger name="WebSite" minlevel="Trace" writeTo="AsyncServerSite" />
		<logger name="Microsoft.Hosting.Lifetime" minlevel="Info" writeTo="AsyncServerSite" final="true" />

		<logger name="Microsoft.*" maxlevel="Info" final="true" />

	</rules>
</nlog>
```

And Finally, a simple use can be this:

```
private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

_logger.Info("Hello");

```
