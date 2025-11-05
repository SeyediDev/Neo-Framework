using Neo.Application.Behaviours.MediatR;
using Neo.Application.Features.GenericEntity.Commands;
using Neo.Application.Features.GenericEntity.GenericService;
using Neo.Application.Features.GenericEntity.Queries;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Queue;
using Neo.Application.Features.Queue.Implementation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Neo.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddNeoApplicationServices(this IServiceCollection services, IConfiguration configuration, params Assembly[] assemblies)
    {
        List<Assembly> assembliesList = [.. assemblies, typeof(DependencyInjection).Assembly];
        assembliesList.Add(typeof(DependencyInjection).Assembly);
        foreach (var assembly in assembliesList)
        {
            services.AddAutoMapper(assembly);
            services.AddValidatorsFromAssembly(assembly);
        }

        services.AddMediatR(cfg =>
        {
            foreach (var assembly in assembliesList)
            {
                cfg.RegisterServicesFromAssembly(assembly);
            }
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehaviour<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(MediatRTelementryBehaviour<,>));
            //وقتی دونه مصرف داشت فعالش کن
            //cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(PublishEventsOfCommandsBehaviour<,>));
        });

        services.AddScoped<IGenericServiceHandler, GenericServiceHandler>();
        services.AddScoped(typeof(IGetGenericEntityCommandHandler<,,>), typeof(GetGenericEntityCommandHandler<,,>));
        services.AddScoped(typeof(IGetAllGenericEntityCommandHandler<,,>), typeof(GetAllGenericEntityCommandHandler<,,>));
        services.AddScoped(typeof(ICreateGenericEntityCommandHandler<,,>), typeof(CreateGenericEntityCommandHandler<,,>));
        services.AddScoped(typeof(IDeleteGenericEntityCommandHandler<,>), typeof(DeleteGenericEntityCommandHandler<,>));
        services.AddScoped(typeof(IUpdateGenericEntityCommandHandler<,,>), typeof(UpdateGenericEntityCommandHandler<,,>));

        services.AddScoped(typeof(IOutboxMessageProcessor<>), typeof(OutboxMessageProcessor<>));
        services.AddScopedWithTelemetry<IOutboxJobScheduler, DefaultOutboxJobScheduler>();
        services.AddScopedWithTelemetry<IJobCommand, JobCommand>();
        services.AddScopedWithTelemetry<IJobPublisher, JobPublisher>();
        services.AddScoped<IOutboxStore, OutboxStore>();
        
        services.AddScopedWithTelemetry<IProcessOutboxRecurringJob, ProcessOutboxRecurringJob>();
        return services;
    }
}
