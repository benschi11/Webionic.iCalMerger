using Microsoft.AspNetCore.Identity;

namespace Webionic.ICalMerger.Users;

/// <summary>Deutsche Fehlermeldungen für ASP.NET Core Identity.</summary>
public sealed class GermanIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Error(nameof(DefaultError), "Ein unbekannter Fehler ist aufgetreten.");

    public override IdentityError ConcurrencyFailure() => Error(nameof(ConcurrencyFailure), "Die Daten wurden zwischenzeitlich geändert. Bitte noch einmal versuchen.");

    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), "Das Passwort ist falsch.");

    public override IdentityError InvalidToken() => Error(nameof(InvalidToken), "Der Link ist ungültig oder abgelaufen.");

    public override IdentityError LoginAlreadyAssociated() => Error(nameof(LoginAlreadyAssociated), "Diese Anmeldung gehört bereits zu einem Konto.");

    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), "Der Benutzername ist ungültig.");

    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), "Das ist keine gültige E-Mail-Adresse.");

    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), "Diese E-Mail-Adresse ist bereits vergeben.");

    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), "Diese E-Mail-Adresse ist bereits vergeben.");

    public override IdentityError InvalidRoleName(string? role) => Error(nameof(InvalidRoleName), "Der Rollenname ist ungültig.");

    public override IdentityError DuplicateRoleName(string role) => Error(nameof(DuplicateRoleName), "Diese Rolle gibt es bereits.");

    public override IdentityError UserAlreadyHasPassword() => Error(nameof(UserAlreadyHasPassword), "Dieses Konto hat bereits ein Passwort.");

    public override IdentityError UserLockoutNotEnabled() => Error(nameof(UserLockoutNotEnabled), "Für dieses Konto ist die Sperre nicht aktiviert.");

    public override IdentityError UserAlreadyInRole(string role) => Error(nameof(UserAlreadyInRole), "Das Konto hat diese Rolle bereits.");

    public override IdentityError UserNotInRole(string role) => Error(nameof(UserNotInRole), "Das Konto hat diese Rolle nicht.");

    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), $"Das Passwort muss mindestens {length} Zeichen lang sein.");

    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric), "Das Passwort braucht mindestens ein Sonderzeichen.");

    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit), "Das Passwort braucht mindestens eine Ziffer.");

    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower), "Das Passwort braucht mindestens einen Kleinbuchstaben.");

    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper), "Das Passwort braucht mindestens einen Großbuchstaben.");

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Error(nameof(PasswordRequiresUniqueChars), $"Das Passwort braucht mindestens {uniqueChars} verschiedene Zeichen.");

    public override IdentityError RecoveryCodeRedemptionFailed() => Error(nameof(RecoveryCodeRedemptionFailed), "Der Wiederherstellungscode ist ungültig.");

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}
