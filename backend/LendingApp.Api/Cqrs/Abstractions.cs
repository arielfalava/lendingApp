namespace LendingApp.Api.Cqrs;

/// <summary>A request that changes state. Handled by exactly one <see cref="ICommandHandler{TCommand,TResult}"/>.</summary>
public interface ICommand<TResult>;

/// <summary>A request that only reads state. Handled by exactly one <see cref="IQueryHandler{TQuery,TResult}"/>.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> Handle(TCommand command, CancellationToken ct);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken ct);
}

/// <summary>Return type for commands that produce no value.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = new();
}

public interface IDispatcher
{
    Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default);
    Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}

public class NotFoundException(string entity, Guid id) : Exception($"{entity} '{id}' was not found.");

public class ValidationException(IDictionary<string, string[]> errors) : Exception("One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public static void ThrowIfAny(Dictionary<string, List<string>> errors)
    {
        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }
}

/// <summary>Small helper for collecting validation errors inside handlers.</summary>
public sealed class Rules
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public Rules Require(bool condition, string field, string message)
    {
        if (!condition)
        {
            if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
            list.Add(message);
        }
        return this;
    }

    public void ThrowIfInvalid() => ValidationException.ThrowIfAny(_errors);
}
