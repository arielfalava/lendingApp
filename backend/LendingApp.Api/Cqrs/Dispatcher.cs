using System.Collections.Concurrent;
using System.Reflection;

namespace LendingApp.Api.Cqrs;

/// <summary>
/// Resolves the single handler registered for a command or query and invokes it.
/// A tiny in-house replacement for MediatR so the project has no licensing strings attached.
/// </summary>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<(Type, Type), MethodInfo> HandleMethods = new();

    public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default)
        => Invoke<TResult>(typeof(ICommandHandler<,>), command, ct);

    public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default)
        => Invoke<TResult>(typeof(IQueryHandler<,>), query, ct);

    private Task<TResult> Invoke<TResult>(Type openHandler, object request, CancellationToken ct)
    {
        var handlerType = openHandler.MakeGenericType(request.GetType(), typeof(TResult));
        var handler = services.GetService(handlerType)
            ?? throw new InvalidOperationException($"No handler registered for {request.GetType().Name}.");
        var method = HandleMethods.GetOrAdd((openHandler, handlerType), k => k.Item2.GetMethod("Handle")!);
        return (Task<TResult>)method.Invoke(handler, [request, ct])!;
    }
}

public static class CqrsServiceCollectionExtensions
{
    /// <summary>Registers the dispatcher and every command/query handler found in the assembly.</summary>
    public static IServiceCollection AddCqrs(this IServiceCollection services, Assembly assembly)
    {
        services.AddScoped<IDispatcher, Dispatcher>();

        var handlerInterfaces = new[] { typeof(ICommandHandler<,>), typeof(IQueryHandler<,>) };
        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (var iface in type.GetInterfaces()
                         .Where(i => i.IsGenericType && handlerInterfaces.Contains(i.GetGenericTypeDefinition())))
            {
                services.AddScoped(iface, type);
            }
        }
        return services;
    }
}
