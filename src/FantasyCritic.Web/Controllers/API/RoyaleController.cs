using FantasyCritic.Lib.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using FantasyCritic.Lib.Extensions;
using FantasyCritic.Lib.Identity;
using FantasyCritic.Lib.Royale;
using FantasyCritic.Lib.Utilities;
using FantasyCritic.Web.Models.Requests.Royale;
using FantasyCritic.Web.Models.Responses;
using FantasyCritic.Web.Models.Responses.Royale;

namespace FantasyCritic.Web.Controllers.API;

[Route("api/[controller]/[action]")]
[Authorize]
public class RoyaleController : FantasyCriticController
{
    private readonly IClock _clock;
    private readonly ILogger<RoyaleController> _logger;
    private readonly RoyaleService _royaleService;
    private readonly InterLeagueService _interLeagueService;
    private static readonly SemaphoreSlim _royaleSemaphore = new SemaphoreSlim(1, 1);

    public RoyaleController(IClock clock, ILogger<RoyaleController> logger, FantasyCriticUserManager userManager,
        RoyaleService royaleService, InterLeagueService interLeagueService) : base(userManager)
    {
        _clock = clock;
        _logger = logger;
        _royaleService = royaleService;
        _interLeagueService = interLeagueService;
    }

    [AllowAnonymous]
    public async Task<IActionResult> RoyaleQuarters()
    {
        IReadOnlyList<RoyaleYearQuarter> supportedQuarters = await _royaleService.GetYearQuarters();
        var viewModels = supportedQuarters.Select(x => new RoyaleYearQuarterViewModel(x));
        return Ok(viewModels);
    }

    [AllowAnonymous]
    public async Task<IActionResult> ActiveRoyaleQuarter()
    {
        var activeQuarter = await _royaleService.GetActiveYearQuarter();
        var viewModel = new RoyaleYearQuarterViewModel(activeQuarter);
        return Ok(viewModel);
    }

    [AllowAnonymous]
    [HttpGet("{year}/{quarter}")]
    public async Task<IActionResult> RoyaleQuarter(int year, int quarter)
    {
        var requestedQuarter = await _royaleService.GetYearQuarter(year, quarter);
        if (requestedQuarter is null)
        {
            return NotFound();
        }

        var viewModel = new RoyaleYearQuarterViewModel(requestedQuarter);
        return Ok(viewModel);
    }

    [HttpPost]
    [Authorize("Write")]
    public async Task<IActionResult> CreateRoyalePublisher([FromBody] CreateRoyalePublisherRequest request)
    {
        var currentUser = await GetCurrentUserOrThrow();

        if (string.IsNullOrWhiteSpace(request.PublisherName))
        {
            return BadRequest("You cannot have a blank name.");
        }

        IReadOnlyList<RoyaleYearQuarter> supportedQuarters = await _royaleService.GetYearQuarters();
        var selectedQuarter = supportedQuarters.Single(x => x.YearQuarter.Year == request.Year && x.YearQuarter.Quarter == request.Quarter);
        if (!selectedQuarter.OpenForPlay || selectedQuarter.Finished)
        {
            return BadRequest();
        }

        var existingPublisher = await _royaleService.GetPublisher(selectedQuarter, currentUser);
        if (existingPublisher is not null)
        {
            return BadRequest();
        }

        RoyalePublisher publisher = await _royaleService.CreatePublisher(selectedQuarter, currentUser, request.PublisherName);
        return Ok(publisher.PublisherID);
    }

    [HttpPost]
    [Authorize("Write")]
    public async Task<IActionResult> ChangePublisherName([FromBody] ChangeRoyalePublisherNameRequest request)
    {
        var currentUser = await GetCurrentUserOrThrow();

        if (string.IsNullOrWhiteSpace(request.PublisherName))
        {
            return BadRequest("You cannot have a blank name.");
        }

        RoyalePublisher? publisher = await _royaleService.GetPublisher(request.PublisherID);
        if (publisher is null)
        {
            return NotFound();
        }

        if (!publisher.User.Equals(currentUser))
        {
            return Forbid();
        }

        await _royaleService.ChangePublisherName(publisher, request.PublisherName);
        return Ok();
    }

    [HttpPost]
    [Authorize("Write")]
    public async Task<IActionResult> ChangePublisherIcon([FromBody] ChangeRoyalePublisherIconRequest request)
    {
        var currentUser = await GetCurrentUserOrThrow();

        RoyalePublisher? publisher = await _royaleService.GetPublisher(request.PublisherID);
        if (publisher is null)
        {
            return NotFound();
        }

        if (!publisher.User.Equals(currentUser))
        {
            return Forbid();
        }

        await _royaleService.ChangePublisherIcon(publisher, request.PublisherIcon);
        return Ok();
    }

    [HttpPost]
    [Authorize("Write")]
    public async Task<IActionResult> ChangePublisherSlogan([FromBody] ChangeRoyalePublisherSloganRequest request)
    {
        var currentUser = await GetCurrentUserOrThrow();

        RoyalePublisher? publisher = await _royaleService.GetPublisher(request.PublisherID);
        if (publisher is null)
        {
            return NotFound();
        }

        if (!publisher.User.Equals(currentUser))
        {
            return Forbid();
        }

        await _royaleService.ChangePublisherSlogan(publisher, request.PublisherSlogan);
        return Ok();
    }

    [HttpGet("{year}/{quarter}")]
    public async Task<IActionResult> GetUserRoyalePublisher(int year, int quarter)
    {
        var currentUser = await GetCurrentUserOrThrow();

        var yearQuarter = await _royaleService.GetYearQuarter(year, quarter);
        if (yearQuarter is null)
        {
            return BadRequest();
        }

        RoyalePublisher? publisher = await _royaleService.GetPublisher(yearQuarter, currentUser);
        if (publisher is null)
        {
            return NotFound();
        }

        IReadOnlyList<RoyaleYearQuarter> quartersWon = await _royaleService.GetQuartersWonByUser(publisher.User);
        var currentDate = _clock.GetToday();
        var masterGameTags = await _interLeagueService.GetMasterGameTags();
        var viewModel = new RoyalePublisherViewModel(publisher, currentDate, null, quartersWon, masterGameTags, true);
        return Ok(viewModel);
    }

    [AllowAnonymous]
    [HttpGet("{id}")]
    public async Task<IActionResult> GetRoyalePublisher(Guid id)
    {
        RoyalePublisher? publisher = await _royaleService.GetPublisher(id);
        if (publisher is null)
        {
            return NotFound();
        }

        IReadOnlyList<RoyaleYearQuarter> quartersWon = await _royaleService.GetQuartersWonByUser(publisher.User);
        var currentDate = _clock.GetToday();
        var masterGameTags = await _interLeagueService.GetMasterGameTags();
        bool thisPlayerIsViewing = false;
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsSuccess)
        {
            thisPlayerIsViewing = currentUserResult.Value.Id == publisher.User.Id;
        }

        var viewModel = new RoyalePublisherViewModel(publisher, currentDate, null, quartersWon, masterGameTags, thisPlayerIsViewing);
        return Ok(viewModel);
    }

    [AllowAnonymous]
    [HttpGet("{year}/{quarter}")]
    public async Task<IActionResult> RoyaleStandings(int year, int quarter)
    {
        IReadOnlyList<RoyalePublisher> publishers = await _royaleService.GetAllPublishers(year, quarter);
        var publishersToShow = publishers.Where(x => x.PublisherGames.Any()).OrderByDescending(x => x.GetTotalFantasyPoints());
        int ranking = 1;
        List<RoyalePublisherViewModel> viewModels = new List<RoyalePublisherViewModel>();
        var masterGameTags = await _interLeagueService.GetMasterGameTags();
        IReadOnlyDictionary<FantasyCriticUser, IReadOnlyList<RoyaleYearQuarter>> previousWinners = await _royaleService.GetRoyaleWinners();
        var currentUserResult = await GetCurrentUser();
        foreach (var publisher in publishersToShow)
        {
            int? thisRanking = null;
            if (publisher.GetTotalFantasyPoints() > 0)
            {
                thisRanking = ranking;
                ranking++;
            }

            var winningQuarters = previousWinners.GetValueOrDefault(publisher.User, new List<RoyaleYearQuarter>());
            bool thisPlayerIsViewing = false;
            if (currentUserResult.IsSuccess)
            {
                thisPlayerIsViewing = currentUserResult.Value.Id == publisher.User.Id;
            }

            var currentDate = _clock.GetToday();
            var vm = new RoyalePublisherViewModel(publisher, currentDate, thisRanking, winningQuarters, masterGameTags, thisPlayerIsViewing);
            viewModels.Add(vm);
        }

        return Ok(viewModels);
    }

    [HttpPost]
    [Authorize("Write")]
    public async Task<IActionResult> PurchaseGame([FromBody] PurchaseRoyaleGameRequest request)
    {
        var currentUser = await GetCurrentUserOrThrow();

        RoyalePublisher? publisher = await _royaleService.GetPublisher(request.PublisherID);
        if (publisher is null)
        {
            return NotFound();
        }

        if (!publisher.User.Equals(currentUser))
        {
            return Forbid();
        }

        var masterGame = await _interLeagueService.GetMasterGameYear(request.MasterGameID, publisher.YearQuarter.YearQuarter.Year);
        if (masterGame is null)
        {
            return NotFound();
        }

        await _royaleSemaphore.WaitAsync();
        try
        {
            var purchaseResult = await _royaleService.PurchaseGame(publisher, masterGame);
            var viewModel = new PlayerClaimResultViewModel(purchaseResult);
            return Ok(viewModel);
        }
        finally
        {
            _royaleSemaphore.Release(1);
        }
    }

    [HttpPost]
    [Authorize("Write")]
    public async Task<IActionResult> SellGame([FromBody] SellRoyaleGameRequest request)
    {
        var currentUser = await GetCurrentUserOrThrow();

        RoyalePublisher? publisher = await _royaleService.GetPublisher(request.PublisherID);
        if (publisher is null)
        {
            return NotFound();
        }

        if (!publisher.User.Equals(currentUser))
        {
            return Forbid();
        }

        await _royaleSemaphore.WaitAsync();
        try
        {
            var publisherGame = publisher.PublisherGames.SingleOrDefault(x => x.MasterGame.MasterGame.MasterGameID == request.MasterGameID);
            if (publisherGame is null)
            {
                return BadRequest();
            }

            var sellResult = await _royaleService.SellGame(publisher, publisherGame);
            if (sellResult.IsFailure)
            {
                return BadRequest(sellResult.Error);
            }

            return Ok();
        }
        finally
        {
            _royaleSemaphore.Release(1);
        }
    }

    [HttpPost]
    [Authorize("Write")]
    public async Task<IActionResult> SetAdvertisingMoney([FromBody] SetAdvertisingMoneyRequest request)
    {
        var currentUser = await GetCurrentUserOrThrow();

        RoyalePublisher? publisher = await _royaleService.GetPublisher(request.PublisherID);
        if (publisher is null)
        {
            return NotFound();
        }

        if (!publisher.User.Equals(currentUser))
        {
            return Forbid();
        }

        await _royaleSemaphore.WaitAsync();
        try
        {
            var publisherGame = publisher.PublisherGames.SingleOrDefault(x => x.MasterGame.MasterGame.MasterGameID == request.MasterGameID);
            if (publisherGame is null)
            {
                return BadRequest();
            }

            var truncatedRequest = request.AdvertisingMoney.TruncateToPrecision(2);
            var setAdvertisingMoneyResult = await _royaleService.SetAdvertisingMoney(publisher, publisherGame, truncatedRequest);
            if (setAdvertisingMoneyResult.IsFailure)
            {
                return BadRequest(setAdvertisingMoneyResult.Error);
            }

            return Ok();
        }
        finally
        {
            _royaleSemaphore.Release(1);
        }
    }

    public async Task<ActionResult<List<PossibleRoyaleMasterGameViewModel>>> PossibleMasterGames(string? gameName, Guid publisherID)
    {
        RoyalePublisher? publisher = await _royaleService.GetPublisher(publisherID);
        if (publisher is null)
        {
            return NotFound();
        }

        var yearQuarter = await _royaleService.GetYearQuarter(publisher.YearQuarter.YearQuarter.Year, publisher.YearQuarter.YearQuarter.Quarter);
        if (yearQuarter is null)
        {
            return BadRequest();
        }

        var currentDate = _clock.GetToday();
        var masterGameTags = await _interLeagueService.GetMasterGameTags();
        var masterGames = await _royaleService.GetMasterGamesForYearQuarter(yearQuarter.YearQuarter);
        if (!string.IsNullOrWhiteSpace(gameName))
        {
            masterGames = MasterGameSearching.SearchMasterGameYears(gameName, masterGames);
        }
        else
        {
            masterGames = masterGames
                .Where(x => x.WillReleaseInQuarter(yearQuarter.YearQuarter))
                .Where(x => !x.MasterGame.IsReleased(currentDate))
                .Where(x => !LeagueTagExtensions.GetRoyaleClaimErrors(masterGameTags, x.MasterGame, currentDate).Any())
                .Take(1000)
                .ToList();
        }

        var viewModels = masterGames.Select(masterGame =>
            new PossibleRoyaleMasterGameViewModel(masterGame, currentDate, yearQuarter, publisher.PublisherGames.Any(y =>
                y.MasterGame.MasterGame.Equals(masterGame.MasterGame)), masterGameTags)).ToList();
        return viewModels;
    }
}
