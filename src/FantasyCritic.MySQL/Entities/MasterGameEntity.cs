using FantasyCritic.Lib.Domain;
using FantasyCritic.Lib.Identity;
using NodaTime;

namespace FantasyCritic.MySQL.Entities;

public class MasterGameEntity
{
    public MasterGameEntity()
    {

    }

    public MasterGameEntity(MasterGame masterGame)
    {
        MasterGameID = masterGame.MasterGameID;
        GameName = masterGame.GameName;
        EstimatedReleaseDate = masterGame.EstimatedReleaseDate;
        MinimumReleaseDate = masterGame.MinimumReleaseDate;
        MaximumReleaseDate = masterGame.MaximumReleaseDate;
        EarlyAccessReleaseDate = masterGame.EarlyAccessReleaseDate;
        InternationalReleaseDate = masterGame.InternationalReleaseDate;
        AnnouncementDate = masterGame.AnnouncementDate;
        ReleaseDate = masterGame.ReleaseDate;
        OpenCriticID = masterGame.OpenCriticID;
        GGToken = masterGame.GGToken;
        CriticScore = masterGame.CriticScore;
        Notes = masterGame.Notes;
        BoxartFileName = masterGame.BoxartFileName;
        GGCoverArtFileName = masterGame.GGCoverArtFileName;

        FirstCriticScoreTimestamp = masterGame.FirstCriticScoreTimestamp;
        DoNotRefreshDate = masterGame.DoNotRefreshDate;
        DoNotRefreshAnything = masterGame.DoNotRefreshAnything;
        DelayContention = masterGame.DelayContention;
        EligibilityChanged = masterGame.EligibilityChanged;
        AddedTimestamp = masterGame.AddedTimestamp;
        AddedByUserID = masterGame.AddedByUser.Id;
    }

    public MasterGameEntity(MasterGame masterGame, Guid addedByUserIDOverride)
    : this(masterGame)
    {
        AddedByUserID = addedByUserIDOverride;
    }

    public Guid MasterGameID { get; set; }
    public string GameName { get; set; } = null!;
    public string EstimatedReleaseDate { get; set; } = null!;
    public LocalDate MinimumReleaseDate { get; set; }
    public LocalDate? MaximumReleaseDate { get; set; }
    public LocalDate? EarlyAccessReleaseDate { get; set; }
    public LocalDate? InternationalReleaseDate { get; set; }
    public LocalDate? AnnouncementDate { get; set; }
    public LocalDate? ReleaseDate { get; set; }
    public int? OpenCriticID { get; set; }
    public string? GGToken { get; set; }
    public decimal? CriticScore { get; set; }
    public bool HasAnyReviews { get; set; }
    public string? OpenCriticSlug { get; set; }
    public string? Notes { get; set; }
    public string? BoxartFileName { get; set; }
    public string? GGCoverArtFileName { get; set; }
    public Instant? FirstCriticScoreTimestamp { get; set; }
    public bool DoNotRefreshDate { get; set; }
    public bool DoNotRefreshAnything { get; set; }
    public bool EligibilityChanged { get; set; }
    public bool DelayContention { get; set; }
    public Instant AddedTimestamp { get; set; }
    public Guid AddedByUserID { get; set; }

    public MasterGame ToDomain(IEnumerable<MasterSubGame> subGames, IEnumerable<MasterGameTag> tags, FantasyCriticUser addedByUser)
    {
        return new MasterGame(MasterGameID, GameName, EstimatedReleaseDate, MinimumReleaseDate, MaximumReleaseDate, EarlyAccessReleaseDate, InternationalReleaseDate,
            AnnouncementDate, ReleaseDate, OpenCriticID, GGToken, CriticScore, HasAnyReviews, OpenCriticSlug, Notes, BoxartFileName, GGCoverArtFileName, FirstCriticScoreTimestamp,
            DoNotRefreshDate, DoNotRefreshAnything, EligibilityChanged, DelayContention, AddedTimestamp, addedByUser, subGames, tags);
    }
}
