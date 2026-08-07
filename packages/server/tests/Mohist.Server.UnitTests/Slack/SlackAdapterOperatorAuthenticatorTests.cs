using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackAdapterOperatorAuthenticatorTests
{
    [Fact]
    public async Task Resolved_principal_and_operator_id_return_the_trimmed_identity()
    {
        var auth = new SlackAdapterOperatorAuthenticator();
        var context = NewContext(
            principal: new MohistPrincipal("admin", PrincipalKind.Admin, "admin", [Scope.Operator]),
            operatorId: "  operator-1  ");

        Assert.Equal("operator-1", await auth.AuthenticateAsync(context));
    }

    [Fact]
    public async Task Missing_principal_is_rejected()
    {
        var auth = new SlackAdapterOperatorAuthenticator();
        var context = NewContext(
            principal: null,
            operatorId: "operator-1");

        Assert.Null(await auth.AuthenticateAsync(context));
    }

    [Fact]
    public async Task Missing_or_blank_operator_id_is_rejected()
    {
        var auth = new SlackAdapterOperatorAuthenticator();
        var withoutId = NewContext(
            principal: new MohistPrincipal("admin", PrincipalKind.Admin, "admin", [Scope.Operator]),
            operatorId: null);
        var blankId = NewContext(
            principal: new MohistPrincipal("admin", PrincipalKind.Admin, "admin", [Scope.Operator]),
            operatorId: "   ");

        Assert.Null(await auth.AuthenticateAsync(withoutId));
        Assert.Null(await auth.AuthenticateAsync(blankId));
    }

    [Fact]
    public async Task Repeated_operator_id_header_values_are_rejected()
    {
        var auth = new SlackAdapterOperatorAuthenticator();
        var context = NewContext(
            principal: new MohistPrincipal("admin", PrincipalKind.Admin, "admin", [Scope.Operator]),
            operatorId: "operator-1");
        context.Request.Headers.Append(SlackAdapterOperatorAuthenticator.OperatorIdHeaderName, "operator-2");

        Assert.Null(await auth.AuthenticateAsync(context));
    }

    private static DefaultHttpContext NewContext(MohistPrincipal? principal, string? operatorId)
    {
        var context = new DefaultHttpContext();
        if (principal is not null)
            context.Items[MohistPrincipal.HttpContextItemKey] = principal;
        if (operatorId is not null)
            context.Request.Headers[SlackAdapterOperatorAuthenticator.OperatorIdHeaderName] = operatorId;
        return context;
    }
}
