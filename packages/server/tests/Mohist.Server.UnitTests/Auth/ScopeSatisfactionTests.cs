using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

/// <summary>
/// The route-scope satisfaction rule (design/auth.md scope table):
/// operator satisfies every declaration, readonly satisfies only
/// readonly-declared GET routes, runner and webhook satisfy their own
/// declarations regardless of method.
/// </summary>
public sealed class ScopeSatisfactionTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Operator_SatisfiesEveryDeclaration_OnAnyMethod(string method)
    {
        var declarations = new[]
        {
            RouteScopeRequirementExtensions.Operator,
            RouteScopeRequirementExtensions.OperatorOrReadonly,
            RouteScopeRequirementExtensions.Runner,
            [Scope.Webhook],
        };

        foreach (var declared in declarations)
        {
            Assert.True(
                ScopeSatisfaction.Satisfies(declared, [Scope.Operator], method),
                $"operator should satisfy [{string.Join(", ", declared.Select(s => s.Name))}] on {method}");
        }
    }

    [Fact]
    public void Readonly_SatisfiesTheReadonlyDeclaration_OnGet()
    {
        Assert.True(ScopeSatisfaction.Satisfies(
            RouteScopeRequirementExtensions.OperatorOrReadonly,
            [Scope.Readonly],
            HttpMethods.Get));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Readonly_NeverSatisfiesTheReadonlyDeclaration_OnWriteMethods(string method)
    {
        Assert.False(ScopeSatisfaction.Satisfies(
            RouteScopeRequirementExtensions.OperatorOrReadonly,
            [Scope.Readonly],
            method));
    }

    [Fact]
    public void Readonly_DoesNotSatisfyTheOperatorDeclaration()
    {
        Assert.False(ScopeSatisfaction.Satisfies(
            RouteScopeRequirementExtensions.Operator,
            [Scope.Readonly],
            HttpMethods.Get));
    }

    [Fact]
    public void Readonly_DoesNotSatisfyTheRunnerDeclaration()
    {
        Assert.False(ScopeSatisfaction.Satisfies(
            RouteScopeRequirementExtensions.Runner,
            [Scope.Readonly],
            HttpMethods.Get));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public void Runner_SatisfiesTheRunnerDeclaration_OnAnyMethod(string method)
    {
        Assert.True(ScopeSatisfaction.Satisfies(
            RouteScopeRequirementExtensions.Runner,
            [Scope.Runner],
            method));
    }

    [Fact]
    public void Runner_DoesNotSatisfyTheReadonlyDeclaration()
    {
        Assert.False(ScopeSatisfaction.Satisfies(
            RouteScopeRequirementExtensions.OperatorOrReadonly,
            [Scope.Runner],
            HttpMethods.Get));
    }

    [Fact]
    public void Webhook_SatisfiesTheWebhookDeclaration()
    {
        Assert.True(ScopeSatisfaction.Satisfies(
            [Scope.Webhook],
            [Scope.Webhook],
            HttpMethods.Post));
    }

    [Fact]
    public void Webhook_DoesNotSatisfyTheReadonlyDeclaration()
    {
        Assert.False(ScopeSatisfaction.Satisfies(
            RouteScopeRequirementExtensions.OperatorOrReadonly,
            [Scope.Webhook],
            HttpMethods.Get));
    }

    [Fact]
    public void EmptyGrantedScope_SatisfiesNothing()
    {
        Assert.False(ScopeSatisfaction.Satisfies(
            RouteScopeRequirementExtensions.OperatorOrReadonly,
            [],
            HttpMethods.Get));
    }
}
