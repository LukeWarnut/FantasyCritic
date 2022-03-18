using FantasyCritic.Lib.Domain.Requests;
using FantasyCritic.Lib.Domain.Results;
using FantasyCritic.Lib.Extensions;
using FantasyCritic.Lib.Identity;
using FantasyCritic.Lib.Services;
using FantasyCritic.Web.Hubs;
using FantasyCritic.Web.Models.Requests.League.Trades;
using FantasyCritic.Web.Models.Requests.LeagueManager;
using FantasyCritic.Web.Models.Requests.Shared;
using FantasyCritic.Web.Models.Responses;
using FantasyCritic.Web.Models.RoundTrip;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace FantasyCritic.Web.Controllers.API;

[Route("api/[controller]/[action]")]
[Authorize]
public class LeagueManagerController : FantasyCriticController
{
    private readonly FantasyCriticUserManager _userManager;
    private readonly FantasyCriticService _fantasyCriticService;
    private readonly InterLeagueService _interLeagueService;
    private readonly LeagueMemberService _leagueMemberService;
    private readonly DraftService _draftService;
    private readonly PublisherService _publisherService;
    private readonly IClock _clock;
    private readonly IHubContext<UpdateHub> _hubContext;
    private readonly EmailSendingService _emailSendingService;
    private readonly GameAcquisitionService _gameAcquisitionService;

    public LeagueManagerController(FantasyCriticUserManager userManager, FantasyCriticService fantasyCriticService, InterLeagueService interLeagueService,
        LeagueMemberService leagueMemberService, DraftService draftService, PublisherService publisherService, IClock clock, IHubContext<UpdateHub> hubContext,
        EmailSendingService emailSendingService, GameAcquisitionService gameAcquisitionService) : base(userManager)
    {
        _userManager = userManager;
        _fantasyCriticService = fantasyCriticService;
        _interLeagueService = interLeagueService;
        _leagueMemberService = leagueMemberService;
        _draftService = draftService;
        _publisherService = publisherService;
        _clock = clock;
        _hubContext = hubContext;
        _emailSendingService = emailSendingService;
        _gameAcquisitionService = gameAcquisitionService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateLeague([FromBody] CreateLeagueRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest("Some of the settings you chose are not valid.");
        }

        if (string.IsNullOrWhiteSpace(request.LeagueName))
        {
            return BadRequest("You cannot have a blank league name.");
        }

        if (request.Tags.Required.Any())
        {
            return BadRequest("Impressive API usage, but required tags are not ready for prime time yet.");
        }

        var supportedYears = await _interLeagueService.GetSupportedYears();
        var selectedSupportedYear = supportedYears.SingleOrDefault(x => x.Year == request.InitialYear);
        if (selectedSupportedYear is null)
        {
            return BadRequest();
        }

        var userIsBetaUser = await _userManager.IsInRoleAsync(currentUser, "BetaTester");
        bool yearIsOpen = selectedSupportedYear.OpenForCreation || (userIsBetaUser && selectedSupportedYear.OpenForBetaUsers);
        if (!yearIsOpen)
        {
            return BadRequest();
        }

        var tagDictionary = await _interLeagueService.GetMasterGameTagDictionary();
        LeagueCreationParameters domainRequest = request.ToDomain(currentUser, tagDictionary);
        var league = await _fantasyCriticService.CreateLeague(domainRequest);
        if (league.IsFailure)
        {
            return BadRequest(league.Error);
        }

        return Ok();
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> AvailableYears(Guid id)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(id);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        IReadOnlyList<SupportedYear> supportedYears = await _interLeagueService.GetSupportedYears();
        var openYears = supportedYears.Where(x => x.OpenForCreation).Select(x => x.Year);
        var availableYears = openYears.Except(league.Value.Years);

        var userIsBetaUser = await _userManager.IsInRoleAsync(currentUser, "BetaTester");
        if (userIsBetaUser)
        {
            var betaYears = supportedYears.Where(x => x.OpenForBetaUsers).Select(x => x.Year);
            availableYears = availableYears.Concat(betaYears).Distinct();
        }

        return Ok(availableYears);
    }

    [HttpPost]
    public async Task<IActionResult> AddNewLeagueYear([FromBody] NewLeagueYearRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (league.Value.Years.Contains(request.Year))
        {
            return BadRequest();
        }

        var supportedYears = await _interLeagueService.GetSupportedYears();
        var selectedSupportedYear = supportedYears.SingleOrDefault(x => x.Year == request.Year);
        if (selectedSupportedYear is null)
        {
            return BadRequest();
        }

        var userIsBetaUser = await _userManager.IsInRoleAsync(currentUser, "BetaTester");
        bool yearIsOpen = selectedSupportedYear.OpenForCreation || (userIsBetaUser && selectedSupportedYear.OpenForBetaUsers);
        if (!yearIsOpen)
        {
            return BadRequest();
        }

        if (!league.Value.Years.Any())
        {
            throw new Exception("League has no initial year.");
        }

        var mostRecentYear = league.Value.Years.Max();
        var mostRecentLeagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, mostRecentYear);
        if (mostRecentLeagueYear.HasNoValue)
        {
            throw new Exception("Most recent league year could not be found");
        }

        var updatedOptions = mostRecentLeagueYear.Value.Options.UpdateOptionsForYear(request.Year);
        await _fantasyCriticService.AddNewLeagueYear(league.Value, request.Year, updatedOptions, mostRecentLeagueYear.Value);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> ChangeLeagueOptions([FromBody] ChangeLeagueOptionsRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        if (string.IsNullOrWhiteSpace(request.LeagueName))
        {
            return BadRequest("You cannot have a blank league name.");
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (league.Value.TestLeague)
        {
            //Users can't change a test league to a non test.
            request.TestLeague = true;
        }

        await _fantasyCriticService.ChangeLeagueOptions(league.Value, request.LeagueName, request.PublicLeague, request.TestLeague);
        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> EditLeagueYearSettings([FromBody] LeagueYearSettingsViewModel request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (request.Tags.Required.Any())
        {
            return BadRequest("Impressive API usage, but required tags are not ready for prime time yet.");
        }

        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var tagDictionary = await _interLeagueService.GetMasterGameTagDictionary();
        EditLeagueYearParameters domainRequest = request.ToDomain(currentUser, tagDictionary);
        Result result = await _fantasyCriticService.EditLeague(leagueYear.Value, domainRequest);
        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        await _fantasyCriticService.UpdatePublisherGameCalculatedStats(leagueYear.Value);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> InvitePlayer([FromBody] CreateInviteRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        string baseURL = $"{Request.Scheme}://{Request.Host.Value}";
        FantasyCriticUser inviteUser;
        if (!request.IsDisplayNameInvite())
        {
            string inviteEmail = request.InviteEmail.ToLower();
            inviteUser = await _userManager.FindByEmailAsync(inviteEmail);
            if (inviteUser is null)
            {
                Result userlessInviteResult = await _leagueMemberService.InviteUserByEmail(league.Value, inviteEmail);
                if (userlessInviteResult.IsFailure)
                {
                    return BadRequest(userlessInviteResult.Error);
                }

                await _emailSendingService.SendSiteInviteEmail(inviteEmail, league.Value, baseURL);
                return Ok();
            }
        }
        else
        {
            inviteUser = await _userManager.FindByDisplayName(request.InviteDisplayName, request.InviteDisplayNumber.Value);
        }

        if (inviteUser is null)
        {
            return BadRequest("No user is found with that information.");
        }

        Result userInviteResult = await _leagueMemberService.InviteUserByUserID(league.Value, inviteUser);
        if (userInviteResult.IsFailure)
        {
            return BadRequest(userInviteResult.Error);
        }

        await _emailSendingService.SendLeagueInviteEmail(inviteUser.Email, league.Value, baseURL);
        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> CreateInviteLink([FromBody] CreateInviteLinkRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        IReadOnlyList<LeagueInviteLink> activeLinks = await _leagueMemberService.GetActiveInviteLinks(league.Value);
        if (activeLinks.Count >= 2)
        {
            return BadRequest("You can't have more than 2 invite links active.");
        }

        await _leagueMemberService.CreateInviteLink(league.Value);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> DeleteInviteLink([FromBody] DeleteInviteLinkRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var activeLinks = await _leagueMemberService.GetActiveInviteLinks(league.Value);
        var thisLink = activeLinks.SingleOrDefault(x => x.InviteID == request.InviteID);
        if (thisLink is null)
        {
            return BadRequest();
        }

        await _leagueMemberService.DeactivateInviteLink(thisLink);

        return Ok();
    }

    [HttpGet("{leagueID}")]
    public async Task<IActionResult> InviteLinks(Guid leagueID)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(leagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        int currentYear = league.Value.Years.Max();
        string baseURL = $"{Request.Scheme}://{Request.Host.Value}";
        IReadOnlyList<LeagueInviteLink> activeLinks = await _leagueMemberService.GetActiveInviteLinks(league.Value);
        var viewModels = activeLinks.Select(x => new LeagueInviteLinkViewModel(x, currentYear, baseURL));
        return Ok(viewModels);
    }

    [HttpPost]
    public async Task<IActionResult> RescindInvite([FromBody] DeleteInviteRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        Maybe<LeagueInvite> invite = await _leagueMemberService.GetInvite(request.InviteID);
        if (invite.HasNoValue)
        {
            return BadRequest();
        }

        if (invite.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        await _leagueMemberService.DeleteInvite(invite.Value);
        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> RemovePlayer([FromBody] PlayerRemoveRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (league.Value.LeagueManager.Id == request.UserID)
        {
            return BadRequest("Can't remove the league manager.");
        }

        var removeUser = await _userManager.FindByIdAsync(request.UserID.ToString());
        if (removeUser == null)
        {
            return BadRequest();
        }

        var playersInLeague = await _leagueMemberService.GetUsersInLeague(league.Value);
        bool userIsInLeague = playersInLeague.Any(x => x.Id == removeUser.Id);
        if (!userIsInLeague)
        {
            return BadRequest("That user is not in that league.");
        }

        await _leagueMemberService.FullyRemovePlayerFromLeague(league.Value, removeUser);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> CreatePublisherForUser([FromBody] CreatePublisherForUserRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.PublisherName))
        {
            return BadRequest("You cannot have a blank name.");
        }

        var userToCreate = await _userManager.FindByIdAsync(request.UserID.ToString());
        if (userToCreate == null)
        {
            return BadRequest();
        }

        bool userIsActive = await _leagueMemberService.UserIsActiveInLeagueYear(leagueYear.Value.League, request.Year, userToCreate);
        if (!userIsActive)
        {
            return BadRequest();
        }

        if (leagueYear.Value.PlayStatus.PlayStarted)
        {
            return BadRequest();
        }

        var publisherForUser = leagueYear.Value.GetUserPublisher(userToCreate);
        if (publisherForUser.HasValue)
        {
            return BadRequest("That player already has a publisher for this this league/year.");
        }

        await _publisherService.CreatePublisher(leagueYear.Value, userToCreate, request.PublisherName);
        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> EditPublisher([FromBody] PublisherEditRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var publisher = leagueYear.Value.GetPublisherByID(request.PublisherID);
        if (publisher.HasNoValue)
        {
            return BadRequest();
        }

        var editValues = request.ToDomain(leagueYear.Value, publisher.Value);
        Result result = await _publisherService.EditPublisher(editValues);
        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> RemovePublisher([FromBody] PublisherRemoveRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var publisher = leagueYear.Value.GetPublisherByID(request.PublisherID);
        if (publisher.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.PlayStatus.PlayStarted)
        {
            return BadRequest();
        }

        await _publisherService.FullyRemovePublisher(leagueYear.Value, publisher.Value);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> SetPlayerActiveStatus([FromBody] LeaguePlayerActiveRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (leagueYear.Value.PlayStatus.PlayStarted)
        {
            return BadRequest("You can't change a player's status for a year that is already started.");
        }

        Dictionary<FantasyCriticUser, bool> userActiveStatus = new Dictionary<FantasyCriticUser, bool>();
        foreach (var userKeyValue in request.ActiveStatus)
        {
            var domainUser = await _userManager.FindByIdAsync(userKeyValue.Key.ToString());
            if (domainUser == null)
            {
                return BadRequest();
            }

            var publisherForUser = leagueYear.Value.GetUserPublisher(domainUser);
            if (publisherForUser.HasValue && !userKeyValue.Value)
            {
                return BadRequest("You must remove a player's publisher before you can set them as inactive.");
            }

            userActiveStatus.Add(domainUser, userKeyValue.Value);
        }

        var result = await _leagueMemberService.SetPlayerActiveStatus(leagueYear.Value, userActiveStatus);
        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> SetAutoDraft([FromBody] ManagerSetAutoDraftRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        foreach (var requestPublisher in request.PublisherAutoDraft)
        {
            var publisher = leagueYear.Value.GetPublisherByID(requestPublisher.Key);
            if (publisher.HasNoValue)
            {
                return Forbid();
            }

            await _publisherService.SetAutoDraft(publisher.Value, requestPublisher.Value);
        }

        var draftComplete = await _draftService.RunAutoDraftAndCheckIfComplete(leagueYear.Value);
        if (draftComplete)
        {
            await _hubContext.Clients.Group(leagueYear.Value.GetGroupName).SendAsync("DraftFinished");
        }

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> ManagerClaimGame([FromBody] ClaimGameRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (!leagueYear.Value.PlayStatus.DraftFinished)
        {
            return BadRequest("You can't manually manage games until after you draft.");
        }

        var publisher = leagueYear.Value.GetPublisherByID(request.PublisherID);
        if (publisher.HasNoValue)
        {
            return BadRequest();
        }

        Maybe<MasterGame> masterGame = Maybe<MasterGame>.None;
        if (request.MasterGameID.HasValue)
        {
            masterGame = await _interLeagueService.GetMasterGame(request.MasterGameID.Value);
        }

        bool counterPickedGameIsManualWillNotRelease = PlayerGameExtensions.CounterPickedGameIsManualWillNotRelease(leagueYear.Value, request.CounterPick, masterGame, false);
        ClaimGameDomainRequest domainRequest = new ClaimGameDomainRequest(leagueYear.Value, publisher.Value, request.GameName, request.CounterPick, counterPickedGameIsManualWillNotRelease, request.ManagerOverride, false, masterGame, null, null);
        ClaimResult result = await _gameAcquisitionService.ClaimGame(domainRequest, true, false, false);
        var viewModel = new ManagerClaimResultViewModel(result);

        await _fantasyCriticService.UpdatePublisherGameCalculatedStats(leagueYear.Value);
        return Ok(viewModel);
    }

    [HttpPost]
    public async Task<IActionResult> ManagerAssociateGame([FromBody] AssociateGameRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (!leagueYear.Value.PlayStatus.DraftFinished)
        {
            return BadRequest("You can't manually manage games until after you draft.");
        }

        var publisher = leagueYear.Value.GetPublisherByID(request.PublisherID);
        if (publisher.HasNoValue)
        {
            return BadRequest();
        }

        var publisherGame = publisher.Value.PublisherGames.SingleOrDefault(x => x.PublisherGameID == request.PublisherGameID);
        if (publisherGame == null)
        {
            return BadRequest();
        }

        Maybe<MasterGame> masterGame = await _interLeagueService.GetMasterGame(request.MasterGameID);
        if (masterGame.HasNoValue)
        {
            return BadRequest();
        }

        AssociateGameDomainRequest domainRequest = new AssociateGameDomainRequest(leagueYear.Value, publisher.Value, publisherGame, masterGame.Value, request.ManagerOverride);

        ClaimResult result = await _gameAcquisitionService.AssociateGame(domainRequest);
        var viewModel = new ManagerClaimResultViewModel(result);

        await _fantasyCriticService.UpdatePublisherGameCalculatedStats(leagueYear.Value);

        return Ok(viewModel);
    }

    [HttpPost]
    public async Task<IActionResult> RemovePublisherGame([FromBody] GameRemoveRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(request.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (!leagueYear.Value.PlayStatus.DraftFinished)
        {
            return BadRequest("You can't manually manage games until you after you draft.");
        }

        var publisher = leagueYear.Value.GetPublisherByID(request.PublisherID);
        if (publisher.HasNoValue)
        {
            return BadRequest();
        }

        var publisherGame = publisher.Value.PublisherGames.SingleOrDefault(x => x.PublisherGameID == request.PublisherGameID);
        if (publisherGame == null)
        {
            return BadRequest();
        }

        Result result = await _publisherService.RemovePublisherGame(leagueYear.Value, publisher.Value, publisherGame);
        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        await _fantasyCriticService.UpdatePublisherGameCalculatedStats(leagueYear.Value);

        return Ok();
    }

    [HttpPost]
    public Task<IActionResult> ManuallyScorePublisherGame([FromBody] ManualPublisherGameScoreRequest request)
    {
        return UpdateManualCriticScore(request.PublisherID, request.PublisherGameID, request.ManualCriticScore, request.LeagueID, request.Year);
    }

    [HttpPost]
    public Task<IActionResult> RemoveManualPublisherGameScore([FromBody] RemoveManualPublisherGameScoreRequest request)
    {
        return UpdateManualCriticScore(request.PublisherID, request.PublisherGameID, null, request.LeagueID, request.Year);
    }

    private async Task<IActionResult> UpdateManualCriticScore(Guid publisherID, Guid publisherGameID, decimal? manualCriticScore, Guid leagueID, int year)
    {
        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(leagueID, year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        if (!leagueYear.Value.PlayStatus.DraftFinished)
        {
            return BadRequest("You can't manually manage games until you after you draft.");
        }

        var publisher = leagueYear.Value.GetPublisherByID(publisherID);
        if (publisher.HasNoValue)
        {
            return BadRequest();
        }

        var publisherGame = publisher.Value.PublisherGames.SingleOrDefault(x => x.PublisherGameID == publisherGameID);
        if (publisherGame == null)
        {
            return BadRequest();
        }

        await _fantasyCriticService.ManuallyScoreGame(publisherGame, manualCriticScore);
        await _fantasyCriticService.UpdatePublisherGameCalculatedStats(leagueYear.Value);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> ManuallySetWillNotRelease([FromBody] ManualPublisherGameWillNotReleaseRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        var publisher = await _publisherService.GetPublisher(request.PublisherID);
        if (publisher.HasNoValue)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(publisher.Value.LeagueYear.League.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        Maybe<LeagueYear> leagueYear = await _fantasyCriticService.GetLeagueYear(publisher.Value.LeagueYear.League.LeagueID, publisher.Value.LeagueYear.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }
        if (!leagueYear.Value.PlayStatus.DraftFinished)
        {
            return BadRequest("You can't manually manage games until after you draft.");
        }

        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        Maybe<PublisherGame> publisherGame = await _publisherService.GetPublisherGame(request.PublisherGameID);
        if (publisherGame.HasNoValue)
        {
            return BadRequest();
        }

        await _fantasyCriticService.ManuallySetWillNotRelease(leagueYear.Value, publisherGame.Value, request.WillNotRelease);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> StartDraft([FromBody] StartDraftRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (!leagueYear.Value.PlayStatus.Equals(PlayStatus.NotStartedDraft))
        {
            return BadRequest();
        }

        var supportedYear = (await _interLeagueService.GetSupportedYears()).SingleOrDefault(x => x.Year == request.Year);
        if (supportedYear is null)
        {
            return BadRequest();
        }

        var activeUsers = await _leagueMemberService.GetActivePlayersForLeagueYear(league.Value, request.Year);

        var publishersInLeague = await _publisherService.GetPublishersInLeagueForYear(leagueYear.Value);
        bool readyToPlay = _draftService.LeagueIsReadyToPlay(supportedYear, publishersInLeague, activeUsers);
        if (!readyToPlay)
        {
            return BadRequest();
        }

        var draftComplete = await _draftService.StartDraft(leagueYear.Value);
        await _hubContext.Clients.Group(leagueYear.Value.GetGroupName).SendAsync("RefreshLeagueYear");

        if (draftComplete)
        {
            await _hubContext.Clients.Group(leagueYear.Value.GetGroupName).SendAsync("DraftFinished");
        }

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> ResetDraft([FromBody] ResetDraftRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.PlayStatus.Equals(PlayStatus.NotStartedDraft) || leagueYear.Value.PlayStatus.Equals(PlayStatus.DraftFinal))
        {
            return BadRequest();
        }

        var supportedYear = (await _interLeagueService.GetSupportedYears()).SingleOrDefault(x => x.Year == request.Year);
        if (supportedYear is null)
        {
            return BadRequest();
        }

        await _draftService.ResetDraft(leagueYear.Value);
        await _hubContext.Clients.Group(leagueYear.Value.GetGroupName).SendAsync("RefreshLeagueYear");

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> SetDraftOrder([FromBody] DraftOrderRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (leagueYear.Value.PlayStatus.PlayStarted)
        {
            return BadRequest();
        }

        var activeUsers = await _leagueMemberService.GetActivePlayersForLeagueYear(league.Value, request.Year);
        var publishersInLeague = await _publisherService.GetPublishersInLeagueForYear(leagueYear.Value);
        var readyToSetDraftOrder = _draftService.LeagueIsReadyToSetDraftOrder(publishersInLeague, activeUsers);
        if (!readyToSetDraftOrder)
        {
            return BadRequest();
        }

        List<KeyValuePair<Publisher, int>> draftPositions = new List<KeyValuePair<Publisher, int>>();
        for (var index = 0; index < request.PublisherDraftPositions.Count; index++)
        {
            var requestPublisher = request.PublisherDraftPositions[index];
            var publisher = publishersInLeague.SingleOrDefault(x => x.PublisherID == requestPublisher);
            if (publisher is null)
            {
                return BadRequest();
            }

            draftPositions.Add(new KeyValuePair<Publisher, int>(publisher, index + 1));
        }

        var result = await _draftService.SetDraftOrder(leagueYear.Value, draftPositions);
        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> ManagerDraftGame([FromBody] ManagerDraftGameRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var publisher = await _publisherService.GetPublisher(request.PublisherID);
        if (publisher.HasNoValue)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(publisher.Value.LeagueYear.League.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, publisher.Value.LeagueYear.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (!leagueYear.Value.PlayStatus.DraftIsActive)
        {
            return BadRequest("You can't draft a game if the draft isn't active.");
        }

        var publishersInLeague = await _publisherService.GetPublishersInLeagueForYear(leagueYear.Value);
        var nextPublisher = _draftService.GetNextDraftPublisher(leagueYear.Value, publishersInLeague);
        if (nextPublisher.HasNoValue)
        {
            return BadRequest("There are no spots open to draft.");
        }

        if (!nextPublisher.Value.Equals(publisher.Value))
        {
            return BadRequest("That publisher is not next up for drafting.");
        }

        Maybe<MasterGame> masterGame = Maybe<MasterGame>.None;
        if (request.MasterGameID.HasValue)
        {
            masterGame = await _interLeagueService.GetMasterGame(request.MasterGameID.Value);
        }

        var draftPhase = _draftService.GetDraftPhase(leagueYear.Value, publishersInLeague);
        if (draftPhase.Equals(DraftPhase.StandardGames))
        {
            if (request.CounterPick)
            {
                return BadRequest("Not drafting counterPicks now.");
            }
        }

        if (draftPhase.Equals(DraftPhase.CounterPicks))
        {
            if (!request.CounterPick)
            {
                return BadRequest("Not drafting standard games now.");
            }
        }

        var draftStatus = _draftService.GetDraftStatus(draftPhase, leagueYear.Value, publishersInLeague);
        bool counterPickedGameIsManualWillNotRelease = PlayerGameExtensions.CounterPickedGameIsManualWillNotRelease(leagueYear.Value, publishersInLeague, request.CounterPick, masterGame, false);
        ClaimGameDomainRequest domainRequest = new ClaimGameDomainRequest(publisher.Value, request.GameName, request.CounterPick, counterPickedGameIsManualWillNotRelease, request.ManagerOverride, false,
            masterGame, draftStatus.DraftPosition, draftStatus.OverallDraftPosition);

        var result = await _draftService.DraftGame(domainRequest, true, leagueYear.Value, publishersInLeague);
        var viewModel = new ManagerClaimResultViewModel(result.Result);
        await _hubContext.Clients.Group(leagueYear.Value.GetGroupName).SendAsync("RefreshLeagueYear");

        if (result.DraftComplete)
        {
            await _hubContext.Clients.Group(leagueYear.Value.GetGroupName).SendAsync("DraftFinished");
        }

        return Ok(viewModel);
    }

    [HttpPost]
    public async Task<IActionResult> SetDraftPause([FromBody] DraftPauseRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (request.Pause)
        {
            if (!leagueYear.Value.PlayStatus.Equals(PlayStatus.Drafting))
            {
                return BadRequest();
            }
        }
        if (!request.Pause)
        {
            if (!leagueYear.Value.PlayStatus.Equals(PlayStatus.DraftPaused))
            {
                return BadRequest();
            }
        }

        await _draftService.SetDraftPause(leagueYear.Value, request.Pause);
        await _hubContext.Clients.Group(leagueYear.Value.GetGroupName).SendAsync("RefreshLeagueYear");

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> UndoLastDraftAction([FromBody] UndoLastDraftActionRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (!leagueYear.Value.PlayStatus.Equals(PlayStatus.DraftPaused))
        {
            return BadRequest("Can only undo when the draft is paused.");
        }

        var publishers = await _publisherService.GetPublishersInLeagueForYear(leagueYear.Value);
        bool hasGames = publishers.SelectMany(x => x.PublisherGames).Any();
        if (!hasGames)
        {
            return BadRequest("Can't undo a drafted game if no games have been drafted.");
        }

        await _draftService.UndoLastDraftAction(publishers);
        await _hubContext.Clients.Group(leagueYear.Value.GetGroupName).SendAsync("RefreshLeagueYear");

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> SetGameEligibilityOverride([FromBody] EligiblityOverrideRequest request)
    {
        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        Maybe<MasterGame> masterGame = await _interLeagueService.GetMasterGame(request.MasterGameID);
        if (masterGame.HasNoValue)
        {
            return BadRequest();
        }

        if (masterGame.Value.ReleaseDate.HasValue && masterGame.Value.ReleaseDate.Value.Year < leagueYear.Value.Year)
        {
            return BadRequest("You can't change the override setting of a game that came out in a previous year.");
        }

        var currentDate = _clock.GetToday();
        var eligibilityFactors = leagueYear.Value.GetEligibilityFactorsForMasterGame(masterGame.Value, currentDate);
        bool alreadyEligible = SlotEligibilityService.GameIsEligibleInLeagueYear(eligibilityFactors);
        bool isAllowing = request.Eligible.HasValue && request.Eligible.Value;
        bool isBanning = request.Eligible.HasValue && !request.Eligible.Value;

        if (isAllowing && alreadyEligible)
        {
            return BadRequest("That game is already eligible in your league.");
        }

        if (isBanning && !alreadyEligible)
        {
            return BadRequest("That game is already ineligible in your league.");
        }

        if (!isAllowing)
        {
            var publishers = await _publisherService.GetPublishersInLeagueForYear(leagueYear.Value);
            var matchingPublisherGame = publishers
                .SelectMany(x => x.PublisherGames)
                .FirstOrDefault(x =>
                    x.MasterGame.HasValue &&
                    x.MasterGame.Value.MasterGame.MasterGameID == masterGame.Value.MasterGameID);
            if (matchingPublisherGame != null)
            {
                return BadRequest("You can't change the override setting of a game that someone in your league has.");
            }
        }

        await _fantasyCriticService.SetEligibilityOverride(leagueYear.Value, masterGame.Value, request.Eligible);
        var refreshedLeagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (refreshedLeagueYear.HasNoValue)
        {
            return BadRequest();
        }
        await _fantasyCriticService.UpdatePublisherGameCalculatedStats(refreshedLeagueYear.Value);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> SetGameTagOverride([FromBody] TagOverrideRequest request)
    {
        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        Maybe<MasterGame> masterGame = await _interLeagueService.GetMasterGame(request.MasterGameID);
        if (masterGame.HasNoValue)
        {
            return BadRequest();
        }

        if (masterGame.Value.ReleaseDate.HasValue && masterGame.Value.ReleaseDate.Value.Year < leagueYear.Value.Year)
        {
            return BadRequest("You can't override the tags of a game that came out in a previous year.");
        }

        IReadOnlyList<MasterGameTag> currentOverrideTags = await _fantasyCriticService.GetTagOverridesForGame(league.Value, leagueYear.Value.Year, masterGame.Value);

        var allTags = await _interLeagueService.GetMasterGameTags();
        var requestedTags = allTags.Where(x => request.Tags.Contains(x.Name)).ToList();
        if (ListExtensions.SequencesContainSameElements(masterGame.Value.Tags, requestedTags))
        {
            return BadRequest("That game already has those exact tags.");
        }

        if (ListExtensions.SequencesContainSameElements(currentOverrideTags, requestedTags))
        {
            return BadRequest("That game is already overriden to have those exact tags.");
        }

        await _fantasyCriticService.SetTagOverride(leagueYear.Value, masterGame.Value, requestedTags);
        var refreshedLeagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (refreshedLeagueYear.HasNoValue)
        {
            return BadRequest();
        }
        await _fantasyCriticService.UpdatePublisherGameCalculatedStats(refreshedLeagueYear.Value);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> PromoteNewLeagueManager([FromBody] PromoteNewLeagueManagerRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var newManager = await _userManager.FindByIdAsync(request.NewManagerUserID.ToString());
        var usersInLeague = await _leagueMemberService.GetUsersInLeague(league.Value);
        if (!usersInLeague.Contains(newManager))
        {
            return BadRequest();
        }

        await _leagueMemberService.TransferLeagueManager(league.Value, newManager);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> PostNewManagerMessage([FromBody] PostNewManagerMessageRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        await _fantasyCriticService.PostNewManagerMessage(leagueYear.Value, request.Message, request.IsPublic);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> DeleteManagerMessage([FromBody] DeleteManagerMessageRequest request)
    {
        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var league = await _fantasyCriticService.GetLeagueByID(request.LeagueID);
        if (league.HasNoValue)
        {
            return BadRequest();
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(league.Value.LeagueID, request.Year);
        if (leagueYear.HasNoValue)
        {
            return BadRequest();
        }

        if (league.Value.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        await _fantasyCriticService.DeleteManagerMessage(request.MessageID);

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> RejectTrade([FromBody] BasicTradeRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        var trade = await _fantasyCriticService.GetTrade(request.TradeID);
        if (trade.HasNoValue)
        {
            return BadRequest();
        }

        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (trade.Value.Proposer.LeagueYear.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var supportedYear = (await _interLeagueService.GetSupportedYears()).SingleOrDefault(x => x.Year == trade.Value.Proposer.LeagueYear.Year);
        if (supportedYear is null)
        {
            return BadRequest();
        }

        if (supportedYear.Finished)
        {
            return BadRequest("That year is already finished");
        }

        Result result = await _fantasyCriticService.RejectTradeByManager(trade.Value);
        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> ExecuteTrade([FromBody] BasicTradeRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
        if (systemWideSettings.ActionProcessingMode)
        {
            return BadRequest();
        }

        var trade = await _fantasyCriticService.GetTrade(request.TradeID);
        if (trade.HasNoValue)
        {
            return BadRequest();
        }

        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return BadRequest(currentUserResult.Error);
        }
        var currentUser = currentUserResult.Value;

        if (trade.Value.Proposer.LeagueYear.League.LeagueManager.Id != currentUser.Id)
        {
            return Forbid();
        }

        var supportedYear = (await _interLeagueService.GetSupportedYears()).SingleOrDefault(x => x.Year == trade.Value.Proposer.LeagueYear.Year);
        if (supportedYear is null)
        {
            return BadRequest();
        }

        if (supportedYear.Finished)
        {
            return BadRequest("That year is already finished");
        }

        Result result = await _fantasyCriticService.ExecuteTrade(trade.Value);
        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        return Ok();
    }

    private async Task<(Maybe<LeagueYearRecord> LeagueYearRecord, Maybe<IActionResult> FailedResult)> GetExistingLeagueYearUserIsManagerFor(Guid leagueID, int year, bool failIfActionProcessing)
    {
        if (!ModelState.IsValid)
        {
            return GetFailedResult<LeagueYearRecord>(BadRequest("Invalid request."));
        }

        var currentUserResult = await GetCurrentUser();
        if (currentUserResult.IsFailure)
        {
            return GetFailedResult<LeagueYearRecord>(BadRequest(currentUserResult.Error));
        }
        var currentUser = currentUserResult.Value;

        if (failIfActionProcessing)
        {
            var systemWideSettings = await _interLeagueService.GetSystemWideSettings();
            if (systemWideSettings.ActionProcessingMode)
            {
                return GetFailedResult<LeagueYearRecord>(BadRequest("Site is in read-only mode while actions process."));
            }
        }

        var leagueYear = await _fantasyCriticService.GetLeagueYear(leagueID, year);
        if (leagueYear.HasNoValue)
        {
            return GetFailedResult<LeagueYearRecord>(BadRequest("League year does not exist."));
        }

        if (leagueYear.Value.League.LeagueManager.Id != currentUser.Id)
        {
            return GetFailedResult<LeagueYearRecord>(Forbid("You are not the manager of that league."));
        }

        return (new LeagueYearRecord(currentUser, leagueYear.Value), Maybe<IActionResult>.None);
    }

    private async Task<(Maybe<LeagueYearPublisherRecord> LeagueYearPublisherRecord, Maybe<IActionResult> FailedResult)> GetExistingPublisherAndLeagueYearUserIsManagerFor(Guid leagueID, int year, Guid publisherID, bool failIfActionProcessing)
    {
        var leagueYearRecord = await GetExistingLeagueYearUserIsManagerFor(leagueID, year, failIfActionProcessing);
        if (leagueYearRecord.FailedResult.HasValue)
        {
            return (Maybe<LeagueYearPublisherRecord>.None, leagueYearRecord.FailedResult);
        }

        var publisher = leagueYearRecord.LeagueYearRecord.Value.LeagueYear.GetPublisherByID(publisherID);
        if (publisher.HasNoValue)
        {
            return GetFailedResult<LeagueYearPublisherRecord>(BadRequest("Publisher does not exist in that league."));
        }

        return (new LeagueYearPublisherRecord(leagueYearRecord.LeagueYearRecord.Value.CurrentUser, leagueYearRecord.LeagueYearRecord.Value.LeagueYear, publisher.Value), Maybe<IActionResult>.None);
    }

    private async Task<(Maybe<LeagueYearPublisherRecord> LeagueYearPublisherRecord, Maybe<IActionResult> FailedResult)> GetExistingPublisherAndLeagueYearUserIsManagerFor(Guid leagueID, int year, FantasyCriticUser userForPublisher, bool failIfActionProcessing)
    {
        var leagueYearRecord = await GetExistingLeagueYearUserIsManagerFor(leagueID, year, failIfActionProcessing);
        if (leagueYearRecord.FailedResult.HasValue)
        {
            return (Maybe<LeagueYearPublisherRecord>.None, leagueYearRecord.FailedResult);
        }

        var publisher = leagueYearRecord.LeagueYearRecord.Value.LeagueYear.GetUserPublisher(userForPublisher);
        if (publisher.HasNoValue)
        {
            return GetFailedResult<LeagueYearPublisherRecord>(BadRequest("That user does not have a publisher in that league."));
        }

        return (new LeagueYearPublisherRecord(leagueYearRecord.LeagueYearRecord.Value.CurrentUser, leagueYearRecord.LeagueYearRecord.Value.LeagueYear, publisher.Value), Maybe<IActionResult>.None);
    }

    private async Task<(Maybe<LeagueYearPublisherGameRecord> LeagueYearPublisherGameRecord, Maybe<IActionResult> FailedResult)> GetExistingPublisherGameAndLeagueYearUserIsManagerFor(Guid leagueID, int year, Guid publisherID, Guid publisherGameID, bool failIfActionProcessing)
    {
        var leagueYearPublisherRecord = await GetExistingPublisherAndLeagueYearUserIsManagerFor(leagueID, year, publisherID, failIfActionProcessing);
        if (leagueYearPublisherRecord.FailedResult.HasValue)
        {
            return (Maybe<LeagueYearPublisherGameRecord>.None, leagueYearPublisherRecord.FailedResult);
        }

        var publisherGame = leagueYearPublisherRecord.LeagueYearPublisherRecord.Value.Publisher.PublisherGames.SingleOrDefault(x => x.PublisherGameID == publisherGameID);
        if (publisherGame is null)
        {
            return GetFailedResult<LeagueYearPublisherGameRecord>(BadRequest("That publisher game does not exist."));
        }

        return (new LeagueYearPublisherGameRecord(leagueYearPublisherRecord.LeagueYearPublisherRecord.Value.CurrentUser, leagueYearPublisherRecord.LeagueYearPublisherRecord.Value.LeagueYear,
            leagueYearPublisherRecord.LeagueYearPublisherRecord.Value.Publisher, publisherGame), Maybe<IActionResult>.None);
    }

    private static (Maybe<T> ValidRecord, Maybe<IActionResult> FailedResult) GetFailedResult<T>(IActionResult failedResult) => (Maybe<T>.None, Maybe<IActionResult>.From(failedResult));
}
