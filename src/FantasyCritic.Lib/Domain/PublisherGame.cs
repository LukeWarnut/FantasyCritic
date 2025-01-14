namespace FantasyCritic.Lib.Domain;

public class PublisherGame
{
    public PublisherGame(Guid publisherID, Guid publisherGameID, string gameName, Instant timestamp, bool counterPick, decimal? manualCriticScore, bool manualWillNotRelease,
        decimal? fantasyPoints, MasterGameYear? masterGame, int slotNumber, int? draftPosition, int? overallDraftPosition, uint? bidAmount, Guid? acquiredInTradeID)
    {
        PublisherID = publisherID;
        PublisherGameID = publisherGameID;
        OriginalGameName = gameName;
        Timestamp = timestamp;
        CounterPick = counterPick;
        ManualCriticScore = manualCriticScore;
        ManualWillNotRelease = manualWillNotRelease;
        FantasyPoints = fantasyPoints;
        MasterGame = masterGame;
        SlotNumber = slotNumber;
        DraftPosition = draftPosition;
        OverallDraftPosition = overallDraftPosition;
        BidAmount = bidAmount;
        AcquiredInTradeID = acquiredInTradeID;
    }

    public Guid PublisherID { get; }
    public Guid PublisherGameID { get; }
    public string OriginalGameName { get; }
    public Instant Timestamp { get; }
    public bool CounterPick { get; }
    public decimal? ManualCriticScore { get; }
    public bool ManualWillNotRelease { get; }
    public decimal? FantasyPoints { get; }
    public MasterGameYear? MasterGame { get; }
    public int SlotNumber { get; }
    public int? DraftPosition { get; }
    public int? OverallDraftPosition { get; }
    public uint? BidAmount { get; }
    public Guid? AcquiredInTradeID { get; }

    public string GameName => MasterGame?.MasterGame.GameName ?? OriginalGameName;

    public bool WillRelease()
    {
        if (ManualWillNotRelease)
        {
            return false;
        }

        if (MasterGame is null)
        {
            return false;
        }

        return MasterGame.WillRelease();
    }

    public FormerPublisherGame GetFormerPublisherGame(Instant removedTimestamp, string removedNote) => new FormerPublisherGame(this, removedTimestamp, removedNote);
    public MasterGameYearWithCounterPick? GetMasterGameYearWithCounterPick()
    {
        if (MasterGame is null)
        {
            return null;
        }

        return new MasterGameYearWithCounterPick(MasterGame, CounterPick);
    }

    public override string ToString() => GameName;
}
