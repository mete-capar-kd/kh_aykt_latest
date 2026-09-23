using System.ComponentModel.DataAnnotations;

namespace Hackathon.Assessment.Api.Auth;

public sealed class EntraIdOptions
{
    public const string SectionName = "EntraId";
    public const string OrganizationPlaceholder = "<ORGANİZASYONDAN-ALINACAK>";

    [Required]
    [Url]
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    [Required]
    public string TenantId { get; set; } = OrganizationPlaceholder;

    [Required]
    public string Audience { get; set; } = OrganizationPlaceholder;

    [Required]
    public string[] RequiredRoles { get; set; } = [];

    [Required]
    public string[] RequiredScopes { get; set; } = [];
}
