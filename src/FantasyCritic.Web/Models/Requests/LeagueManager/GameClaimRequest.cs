using System.ComponentModel.DataAnnotations;

namespace FantasyCritic.Web.Models.Requests.LeagueManager;

public class ClaimGameRequest
{
    [Required]
    public Guid PublisherID { get; set; }
    [Required]
    public string GameName { get; set; }
    [Required]
    public bool CounterPick { get; set; }
    public Guid? MasterGameID { get; set; }
    [Required]
    public bool ManagerOverride { get; set; }
}
