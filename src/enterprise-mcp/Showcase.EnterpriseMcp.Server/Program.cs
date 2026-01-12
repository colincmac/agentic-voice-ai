using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.AspNetCore.ResourceServer.Endpoints;
using Showcase.EnterpriseMcp.Server.Tools;
using Showcase.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

var t = builder.Services
    .AddMicrosoftIdentityWebApiAuthentication(builder.Configuration);
builder.Services.AddAuthProtectedResource(builder.Configuration.GetSection("ProtectedResource"), JwtBearerDefaults.AuthenticationScheme);

builder.Services.AddAuthorization();


builder.Services.AddMcpServer()
    .WithTools<WeatherTools>()
    .WithHttpTransport();

// Configure HttpClientFactory for weather.gov API
builder.Services.AddHttpClient("WeatherApi", client =>
{
    client.BaseAddress = new Uri("https://api.weather.gov");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("weather-tool", "1.0"));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();


app.MapProtectedResourcesDiscovery(JwtBearerDefaults.AuthenticationScheme);

app.MapMcp()
    .RequireAuthorization(JwtBearerDefaults.AuthenticationScheme);

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
