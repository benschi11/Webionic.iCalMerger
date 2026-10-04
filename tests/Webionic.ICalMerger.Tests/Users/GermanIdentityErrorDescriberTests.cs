using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Webionic.ICalMerger.Users;

namespace Webionic.ICalMerger.Tests.Users;

public sealed class GermanIdentityErrorDescriberTests
{
    private static readonly string[] EnglishMarkers = ["Passwords must", "Optimistic concurrency", "An unknown failure", "is invalid", "already taken", "Invalid token"];

    [Fact]
    public void Registered_ReplacesDefaultDescriber()
    {
        using var provider = new ServiceCollection().AddLogging().AddAppIdentity().BuildServiceProvider();

        Assert.IsType<GermanIdentityErrorDescriber>(provider.GetRequiredService<IdentityErrorDescriber>());
    }

    [Fact]
    public void PasswordTooShort_UsesConfiguredLengthInGerman()
    {
        var error = new GermanIdentityErrorDescriber().PasswordTooShort(10);

        Assert.Equal("PasswordTooShort", error.Code);
        Assert.Contains("10", error.Description);
        Assert.Contains("Passwort", error.Description);
        Assert.DoesNotContain("Passwords must", error.Description);
    }

    [Fact]
    public void ConcurrencyFailure_IsGerman()
    {
        var error = new GermanIdentityErrorDescriber().ConcurrencyFailure();

        Assert.Equal("ConcurrencyFailure", error.Code);
        Assert.DoesNotContain("Optimistic", error.Description);
        Assert.Contains("geändert", error.Description);
    }

    [Fact]
    public void AllDescriptions_AreNonEmptyAndNotEnglishDefaults()
    {
        var d = new GermanIdentityErrorDescriber();
        IdentityError[] errors =
        [
            d.DefaultError(), d.ConcurrencyFailure(), d.PasswordMismatch(), d.InvalidToken(), d.LoginAlreadyAssociated(),
            d.InvalidUserName("x"), d.InvalidEmail("x"), d.DuplicateUserName("x"), d.DuplicateEmail("x"),
            d.InvalidRoleName("x"), d.DuplicateRoleName("x"), d.UserAlreadyHasPassword(), d.UserLockoutNotEnabled(),
            d.UserAlreadyInRole("x"), d.UserNotInRole("x"), d.PasswordTooShort(10), d.PasswordRequiresNonAlphanumeric(),
            d.PasswordRequiresDigit(), d.PasswordRequiresLower(), d.PasswordRequiresUpper(), d.PasswordRequiresUniqueChars(3),
            d.RecoveryCodeRedemptionFailed(),
        ];

        foreach (var error in errors)
        {
            Assert.False(string.IsNullOrWhiteSpace(error.Description), error.Code);
            Assert.DoesNotContain(EnglishMarkers, m => error.Description.Contains(m, StringComparison.Ordinal));
        }
    }
}
