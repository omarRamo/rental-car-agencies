namespace CarRental.Web.Components.Admin.Platform;

/// <summary>What the registration dialog gives back: the new agency, its name and the address of its public site.</summary>
public sealed record RegisteredAgency(Guid Id, string Name, string Slug);
