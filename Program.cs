using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UserApi");

var validApiKeys = new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
{
    ["dev-api-key-1"] = "dev-user",
    ["test-api-key-2"] = "tester"
};

// ---------------------------
// Request logging middleware
// ---------------------------
app.Use(async (context, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var method = context.Request.Method;
    var path = context.Request.Path;
    var traceId = context.TraceIdentifier;
    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    await next();

    sw.Stop();

    logger.LogInformation(
        "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms (trace:{TraceId} ip:{RemoteIp})",
        method,
        path,
        context.Response.StatusCode,
        sw.ElapsedMilliseconds,
        traceId,
        remoteIp
    );
});

// ---------------------------
// API key authentication middleware
// ---------------------------
app.Use(async (context, next) =>
{
    var apiKeyHeader = context.Request.Headers["X-Api-Key"].FirstOrDefault();

    if (string.IsNullOrWhiteSpace(apiKeyHeader))
    {
        logger.LogWarning("Missing X-Api-Key header for {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers["WWW-Authenticate"] = "ApiKey";

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Unauthorized",
            Status = StatusCodes.Status401Unauthorized,
            Detail = "Missing X-Api-Key header."
        });

        return;
    }

    var key = apiKeyHeader.Trim();

    if (!validApiKeys.TryGetValue(key, out var owner))
    {
        logger.LogWarning("Invalid X-Api-Key for {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers["WWW-Authenticate"] = "ApiKey";

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Unauthorized",
            Status = StatusCodes.Status401Unauthorized,
            Detail = "Invalid X-Api-Key header."
        });

        return;
    }

    var claims = new[]
    {
        new Claim(ClaimTypes.Name, owner)
    };

    var identity = new ClaimsIdentity(claims, "ApiKey");
    context.User = new ClaimsPrincipal(identity);

    await next();
});

// ---------------------------
// Global exception handler
// ---------------------------
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Internal Server Error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "An unexpected error occurred."
        });
    }
});

// ---------------------------
// Endpoints
// ---------------------------
app.MapGet("/users", (int? page, int? pageSize) =>
{
    int p = page.GetValueOrDefault(1);
    int ps = pageSize.GetValueOrDefault(10);

    if (p < 1) p = 1;
    if (ps < 1) ps = 10;

    var allUsers = UserStorage.Users.Values
        .OrderBy(u => u.Id)
        .ToList();

    var total = allUsers.Count;

    var items = allUsers
        .Skip((p - 1) * ps)
        .Take(ps)
        .Select(u => new UserDto(u.Id, u.UserName, u.UserAge, u.Role))
        .ToList();

    return Results.Ok(new
    {
        page = p,
        pageSize = ps,
        total,
        items
    });
});

app.MapGet("/users/{id:int}", (int id) =>
{
    if (!UserStorage.Users.TryGetValue(id, out var user))
    {
        return Results.NotFound(new ProblemDetails
        {
            Title = "Not Found",
            Status = StatusCodes.Status404NotFound,
            Detail = $"User with id {id} was not found."
        });
    }

    return Results.Ok(user);
});

app.MapPost("/users", async (HttpRequest request) =>
{
    User? newUser;

    try
    {
        newUser = await request.ReadFromJsonAsync<User>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Bad Request",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The request body contains invalid JSON or invalid field types."
        });
    }

    if (newUser is null)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Bad Request",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The request body is empty."
        });
    }

    var validationErrors = ValidateModel(newUser);
    if (validationErrors.Any())
        return Results.ValidationProblem(validationErrors);

    int newId = UserStorage.Users.Any() ? UserStorage.Users.Keys.Max() + 1 : 1;
    newUser.Id = newId;

    if (!UserStorage.Users.TryAdd(newId, newUser))
    {
        throw new InvalidOperationException("Failed to add user to storage.");
    }

    return Results.Created($"/users/{newId}", newUser);
});

app.MapPut("/users/{id:int}", async (int id, HttpRequest request) =>
{
    if (!UserStorage.Users.ContainsKey(id))
    {
        return Results.NotFound(new ProblemDetails
        {
            Title = "Not Found",
            Status = StatusCodes.Status404NotFound,
            Detail = $"User with id {id} was not found."
        });
    }

    User? updatedUser;

    try
    {
        updatedUser = await request.ReadFromJsonAsync<User>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Bad Request",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The request body contains invalid JSON or invalid field types."
        });
    }

    if (updatedUser is null)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Bad Request",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The request body is empty."
        });
    }

    var validationErrors = ValidateModel(updatedUser);
    if (validationErrors.Any())
        return Results.ValidationProblem(validationErrors);

    updatedUser.Id = id;
    UserStorage.Users[id] = updatedUser;

    return Results.Ok(updatedUser);
});

app.MapDelete("/users/{id:int}", (int id) =>
{
    if (!UserStorage.Users.TryRemove(id, out _))
    {
        return Results.NotFound(new ProblemDetails
        {
            Title = "Not Found",
            Status = StatusCodes.Status404NotFound,
            Detail = $"User with id {id} was not found."
        });
    }

    return Results.Ok(new
    {
        message = $"User with id {id} deleted"
    });
});

app.Run();

// ---------------------------
// Validation helper
// ---------------------------
static IDictionary<string, string[]> ValidateModel(object model)
{
    var context = new ValidationContext(model);
    var results = new List<ValidationResult>();

    Validator.TryValidateObject(model, context, results, validateAllProperties: true);

    var errors = new Dictionary<string, string[]>();

    foreach (var result in results)
    {
        var memberName = result.MemberNames.FirstOrDefault() ?? string.Empty;

        if (!errors.ContainsKey(memberName))
        {
            errors[memberName] = new[] { result.ErrorMessage ?? "Invalid value" };
        }
        else
        {
            var list = errors[memberName].ToList();
            list.Add(result.ErrorMessage ?? "Invalid value");
            errors[memberName] = list.ToArray();
        }
    }

    return errors;
}

// ---------------------------
// DTOs and models
// ---------------------------
public record UserDto(int Id, string UserName, int UserAge, string Role);

public class User
{
    public int Id { get; set; }

    [Required(ErrorMessage = "UserName is required")]
    [MinLength(2, ErrorMessage = "UserName must be at least 2 characters long")]
    public string UserName { get; set; } = string.Empty;

    [Range(0, 150, ErrorMessage = "UserAge must be between 0 and 150")]
    public int UserAge { get; set; }

    [Required(ErrorMessage = "Role is required")]
    [MinLength(2, ErrorMessage = "Role must be at least 2 characters long")]
    public string Role { get; set; } = string.Empty;
}

// ---------------------------
// In-memory storage
// ---------------------------
public static class UserStorage
{
    public static ConcurrentDictionary<int, User> Users { get; } = new(new[]
    {
        new KeyValuePair<int, User>(1, new User { Id = 1, UserName = "Alice",   UserAge = 25, Role = "Admin" }),
        new KeyValuePair<int, User>(2, new User { Id = 2, UserName = "Bob",     UserAge = 30, Role = "User" }),
        new KeyValuePair<int, User>(3, new User { Id = 3, UserName = "Charlie", UserAge = 22, Role = "Moderator" }),
        new KeyValuePair<int, User>(4, new User { Id = 4, UserName = "Diana",   UserAge = 28, Role = "User" })
    });
}
