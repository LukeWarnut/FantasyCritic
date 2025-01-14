namespace FantasyCritic.Web.Models.Responses;

public class LeagueWithStatusViewModel
{
    public LeagueWithStatusViewModel(League league, LeagueYear relevantLeagueYear, bool isManager, bool userIsInLeague, bool userIsFollowingLeague)
    {
        LeagueID = league.LeagueID;
        LeagueName = league.LeagueName;
        LeagueManager = new PlayerViewModel(league, league.LeagueManager, false);
        IsManager = isManager;
        Archived = league.Archived;
        Years = league.Years;
        ActiveYear = Years.Max();
        PublicLeague = league.PublicLeague;
        TestLeague = league.TestLeague;
        UserIsInLeague = userIsInLeague;
        UserIsFollowingLeague = userIsFollowingLeague;

        OneShotMode = relevantLeagueYear.Options.OneShotMode;
    }

    public Guid LeagueID { get; }
    public string LeagueName { get; }
    public PlayerViewModel LeagueManager { get; }
    public bool IsManager { get; }
    public IReadOnlyList<int> Years { get; }
    public int ActiveYear { get; }
    public bool PublicLeague { get; }
    public bool TestLeague { get; }
    public bool Archived { get; }
    public bool UserIsInLeague { get; }
    public bool UserIsFollowingLeague { get; }
    public bool OneShotMode { get; }
}
