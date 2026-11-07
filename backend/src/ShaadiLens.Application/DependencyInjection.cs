using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Photos;
using ShaadiLens.Application.Uploads;

namespace ShaadiLens.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<UploadOptions>(config.GetSection(UploadOptions.Section));
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        // Use-case services: one per request (scoped), like the DbContext they use.
        services.AddScoped<EventService>();
        services.AddScoped<UploadService>();
        services.AddScoped<PhotoService>();
        return services;
    }
}
