namespace CarRental.Application.Common.Security;

/// <summary>
/// Provides the actor of the ambient execution context when none was set explicitly
/// (the web host reads the current HTTP request). Returns <see langword="null"/> when there is no ambient context.
/// </summary>
public interface IAmbientUserContextProvider
{
    UserContextSnapshot? GetCurrent();
}
