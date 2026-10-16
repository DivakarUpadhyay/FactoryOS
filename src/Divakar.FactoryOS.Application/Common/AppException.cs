namespace Divakar.FactoryOS.Application.Common;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    LimitReached,
}

/// <summary>An expected failure the API turns into a problem-details response with the matching status code.</summary>
public sealed class AppException(ErrorKind kind, string message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;

    public static AppException Validation(string message) => new(ErrorKind.Validation, message);
    public static AppException NotFound(string what) => new(ErrorKind.NotFound, $"{what} was not found.");
    public static AppException Conflict(string message) => new(ErrorKind.Conflict, message);
    public static AppException Unauthorized(string message = "Sign-in failed.") => new(ErrorKind.Unauthorized, message);
    public static AppException Forbidden(string message = "You do not have permission to do this.") => new(ErrorKind.Forbidden, message);
    public static AppException Limit(string message) => new(ErrorKind.LimitReached, message);
}

public static class Guard
{
    public static string Required(string? value, string field, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            throw AppException.Validation($"{field} is required.");
        }
        if (v.Length > max)
        {
            throw AppException.Validation($"{field} must be at most {max} characters.");
        }
        return v;
    }

    public static string? Optional(string? value, string field, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }
        return v.Length > max ? throw AppException.Validation($"{field} must be at most {max} characters.") : v;
    }

    public static string Code(string? value, string field)
    {
        var v = Required(value, field, 40).ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-z0-9][a-z0-9-]*$"))
        {
            throw AppException.Validation($"{field} may contain only letters, digits and dashes.");
        }
        return v;
    }

    public static string Email(string? value)
    {
        var v = Required(value, "Email", 256).ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(v, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            throw AppException.Validation("Email is not a valid address.");
        }
        return v;
    }

    /// <summary>At least 12 characters with a letter and a digit.</summary>
    public static string Password(string? value)
    {
        if (value is null || value.Length < 12 || !value.Any(char.IsLetter) || !value.Any(char.IsDigit))
        {
            throw AppException.Validation("Password must be at least 12 characters and contain letters and digits.");
        }
        return value.Length > 128 ? throw AppException.Validation("Password must be at most 128 characters.") : value;
    }
}
