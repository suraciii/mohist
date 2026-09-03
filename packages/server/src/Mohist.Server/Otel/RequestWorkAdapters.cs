using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Http;
using Mohist.Server.Infrastructure.Data.Db;
using Orleans;
using Orleans.Runtime;

namespace Mohist.Server.Otel;

public sealed class RequestWorkDbCommandInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count(eventData.Context, command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Count(eventData.Context, command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count(eventData.Context, command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Count(eventData.Context, command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Count(eventData.Context, command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Count(eventData.Context, command);
        return ValueTask.FromResult(result);
    }

    private static void Count(DbContext? context, DbCommand command)
    {
        if (context is MohistDbContext && !IsOtelDatabase(command.Connection?.DataSource))
            RequestWorkScope.Current?.AddDatabaseCalls();
    }

    private static bool IsOtelDatabase(string? dataSource) =>
        dataSource?.EndsWith("otel.db", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class RequestWorkOutgoingGrainCallFilter : IOutgoingGrainCallFilter
{
    public Task Invoke(IOutgoingGrainCallContext context)
    {
        RequestWorkScope.Current?.AddDownstreamCalls();
        return context.Invoke();
    }
}

public sealed class RequestWorkIncomingGrainCallFilter : IIncomingGrainCallFilter
{
    public Task Invoke(IIncomingGrainCallContext context)
    {
        return RequestWorkScope.Current is null
            ? context.Invoke()
            : InvokeWithoutAmbientScopeAsync(context);
    }

    private static async Task InvokeWithoutAmbientScopeAsync(IIncomingGrainCallContext context)
    {
        using var ambient = RequestWorkScope.Push(null);
        await context.Invoke();
    }
}

public sealed class RequestWorkCountingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) 
    {
        RequestWorkScope.Current?.AddDownstreamCalls();
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class RequestWorkHttpMessageHandlerBuilderFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.AdditionalHandlers.Add(new RequestWorkCountingHandler());
    };
}
