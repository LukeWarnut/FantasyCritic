﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using Dapper;
using FantasyCritic.Lib.Domain;
using FantasyCritic.Lib.Interfaces;
using FantasyCritic.Lib.OpenCritic;
using FantasyCritic.MySQL.Entities;
using MySql.Data.MySqlClient;
using NodaTime;

namespace FantasyCritic.MySQL
{
    public class MySQLMasterGameRepo : IMasterGameRepo
    {
        private readonly string _connectionString;
        private IReadOnlyList<EligibilityLevel> _eligibilityLevels;
        private readonly Dictionary<int, Dictionary<Guid, MasterGameYear>> _masterGameYearsCache;
        private readonly IReadOnlyFantasyCriticUserStore _userStore;

        private Dictionary<Guid, MasterGame> _masterGamesCache;

        public MySQLMasterGameRepo(string connectionString, IReadOnlyFantasyCriticUserStore userStore)
        {
            _connectionString = connectionString;
            _userStore = userStore;
            _masterGamesCache = new Dictionary<Guid, MasterGame>();
            _masterGameYearsCache = new Dictionary<int, Dictionary<Guid, MasterGameYear>>();
        }

        public async Task<IReadOnlyList<MasterGame>> GetMasterGames()
        {
            if (_masterGamesCache.Any())
            {
                return _masterGamesCache.Values.ToList();
            }

            using (var connection = new MySqlConnection(_connectionString))
            {
                var masterGameResults = await connection.QueryAsync<MasterGameEntity>("select * from tbl_mastergame;");
                var masterSubGameResults = await connection.QueryAsync<MasterSubGameEntity>("select * from tbl_mastergame_subgame;");

                var masterSubGames = masterSubGameResults.Select(x => x.ToDomain()).ToList();
                List<MasterGame> masterGames = new List<MasterGame>();
                foreach (var entity in masterGameResults)
                {
                    EligibilityLevel eligibilityLevel = await GetEligibilityLevel(entity.EligibilityLevel);
                    MasterGame domain = entity.ToDomain(masterSubGames.Where(sub => sub.MasterGameID == entity.MasterGameID),
                            eligibilityLevel);
                    masterGames.Add(domain);
                }

                _masterGamesCache = masterGames.ToDictionary(x => x.MasterGameID, y => y);
                return masterGames;
            }
        }

        public async Task<IReadOnlyList<MasterGameYear>> GetMasterGameYears(int year)
        {
            if (_masterGameYearsCache.ContainsKey(year))
            {
                return _masterGameYearsCache[year].Values.ToList();
            }

            using (var connection = new MySqlConnection(_connectionString))
            {
                var masterGameResults = await connection.QueryAsync<MasterGameYearEntity>("select * from tbl_caching_mastergameyear where Year = @year;", new { year });
                var masterSubGameResults = await connection.QueryAsync<MasterSubGameEntity>("select * from tbl_mastergame_subgame;");

                var masterSubGames = masterSubGameResults.Select(x => x.ToDomain()).ToList();
                List<MasterGameYear> masterGames = new List<MasterGameYear>();
                foreach (var entity in masterGameResults)
                {
                    EligibilityLevel eligibilityLevel = await GetEligibilityLevel(entity.EligibilityLevel);
                    MasterGameYear domain = entity.ToDomain(masterSubGames.Where(sub => sub.MasterGameID == entity.MasterGameID),
                            eligibilityLevel, year);
                    masterGames.Add(domain);
                }

                _masterGameYearsCache[year] = masterGames.ToDictionary(x => x.MasterGame.MasterGameID, y => y);

                return masterGames;
            }
        }

        public async Task<Maybe<MasterGame>> GetMasterGame(Guid masterGameID)
        {
            if (!_masterGamesCache.Any())
            {
                await GetMasterGames();
            }

            _masterGamesCache.TryGetValue(masterGameID, out MasterGame foundMasterGame);
            if (foundMasterGame is null)
            {
                return Maybe<MasterGame>.None;
            }

            return foundMasterGame;
        }

        public async Task<Maybe<MasterGameYear>> GetMasterGameYear(Guid masterGameID, int year)
        {
            if (!_masterGameYearsCache.ContainsKey(year))
            {
                await GetMasterGameYears(year);
            }

            var yearCache = _masterGameYearsCache[year];
            yearCache.TryGetValue(masterGameID, out MasterGameYear foundMasterGame);
            if (foundMasterGame is null)
            {
                return Maybe<MasterGameYear>.None;
            }

            return foundMasterGame;
        }

        public async Task UpdateCriticStats(MasterGame masterGame, OpenCriticGame openCriticGame)
        {
            DateTime? releaseDate = null;
            if (openCriticGame.ReleaseDate.HasValue)
            {
                releaseDate = openCriticGame.ReleaseDate.Value.ToDateTimeUnspecified();
            }

            string setFirstTimestamp = "";
            if (!masterGame.CriticScore.HasValue && openCriticGame.Score.HasValue)
            {
                setFirstTimestamp = ", FirstCriticScoreTimestamp = CURRENT_TIMESTAMP ";
            }

            string sql = $"update tbl_mastergame set ReleaseDate = @releaseDate, CriticScore = @criticScore {setFirstTimestamp} where MasterGameID = @masterGameID";
            using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.ExecuteAsync(sql,
                    new
                    {
                        masterGameID = masterGame.MasterGameID,
                        releaseDate = releaseDate,
                        criticScore = openCriticGame.Score
                    });
            }
        }

        public async Task UpdateCriticStats(MasterSubGame masterSubGame, OpenCriticGame openCriticGame)
        {
            DateTime? releaseDate = null;
            if (openCriticGame.ReleaseDate.HasValue)
            {
                releaseDate = openCriticGame.ReleaseDate.Value.ToDateTimeUnspecified();
            }

            using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.ExecuteAsync("update tbl_mastergame_subgame set ReleaseDate = @releaseDate, CriticScore = @criticScore where MasterSubGameID = @masterSubGameID",
                    new
                    {
                        masterSubGameID = masterSubGame.MasterSubGameID,
                        releaseDate = releaseDate,
                        criticScore = openCriticGame.Score
                    });
            }
        }

        public async Task CreateMasterGame(MasterGame masterGame)
        {
            var entity = new MasterGameEntity(masterGame);
            using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.ExecuteAsync(
                    "insert into tbl_mastergame(MasterGameID,GameName,EstimatedReleaseDate,ReleaseDate,OpenCriticID,CriticScore,MinimumReleaseYear," +
                    "EligibilityLevel,YearlyInstallment,EarlyAccess,FreeToPlay,ReleasedInternationally,ExpansionPack,BoxartFileName) VALUES " +
                    "(@MasterGameID,@GameName,@EstimatedReleaseDate,@ReleaseDate,@OpenCriticID,@CriticScore,@MinimumReleaseYear," +
                    "@EligibilityLevel,@YearlyInstallment,@EarlyAccess,@FreeToPlay,@ReleasedInternationally,@ExpansionPack,@BoxartFileName);",
                    entity);
            }
        }

        public async Task<EligibilityLevel> GetEligibilityLevel(int eligibilityLevel)
        {
            var eligbilityLevel = await GetEligibilityLevels();
            return eligbilityLevel.Single(x => x.Level == eligibilityLevel);
        }

        public async Task<IReadOnlyList<EligibilityLevel>> GetEligibilityLevels()
        {
            if (_eligibilityLevels != null)
            {
                return _eligibilityLevels;
            }
            using (var connection = new MySqlConnection(_connectionString))
            {
                var entities = await connection.QueryAsync<EligibilityLevelEntity>("select * from tbl_settings_eligibilitylevel;");
                _eligibilityLevels = entities.Select(x => x.ToDomain()).ToList();
                return _eligibilityLevels;
            }
        }

        public async Task<IReadOnlyList<Guid>> GetAllSelectedMasterGameIDsForYear(int year)
        {
            var sql = "select distinct MasterGameID from tbl_league_publishergame " +
                      "join tbl_league_publisher on(tbl_league_publisher.PublisherID = tbl_league_publishergame.PublisherID) " +
                      "join tbl_league on (tbl_league.LeagueID = tbl_league_publisher.LeagueID) " +
                      "where Year = @year and tbl_league.TestLeague = 0";

            using (var connection = new MySqlConnection(_connectionString))
            {
                IEnumerable<Guid> guids = await connection.QueryAsync<Guid>(sql, new { year });
                return guids.ToList();
            }
        }

        public async Task CreateMasterGameRequest(MasterGameRequest domainRequest)
        {
            var entity = new MasterGameRequestEntity(domainRequest);

            using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.ExecuteAsync(
                    "insert into tbl_mastergame_request(RequestID,UserID,RequestTimestamp,RequestNote,GameName,SteamID,OpenCriticID,EstimatedReleaseDate,EligibilityLevel," +
                    "YearlyInstallment,EarlyAccess,FreeToPlay,ReleasedInternationally,ExpansionPack,Answered,ResponseTimestamp,ResponseNote,MasterGameID,Hidden) VALUES " +
                    "(@RequestID,@UserID,@RequestTimestamp,@RequestNote,@GameName,@SteamID,@OpenCriticID,@EstimatedReleaseDate," +
                    "@EligibilityLevel,@YearlyInstallment,@EarlyAccess,@FreeToPlay,@ReleasedInternationally,@ExpansionPack,@Answered,@ResponseTimestamp,@ResponseNote,@MasterGameID,@Hidden);",
                    entity);
            }
        }

        public async Task CreateMasterGameChangeRequest(MasterGameChangeRequest domainRequest)
        {
            var entity = new MasterGameChangeRequestEntity(domainRequest);

            using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.ExecuteAsync(
                    "insert into tbl_mastergame_changerequest(RequestID,UserID,RequestTimestamp,RequestNote,MasterGameID,Answered,ResponseTimestamp,ResponseNote,Hidden) VALUES " +
                    "(@RequestID,@UserID,@RequestTimestamp,@RequestNote,@MasterGameID,@Answered,@ResponseTimestamp,@ResponseNote,@Hidden);",
                    entity);
            }
        }

        public async Task<IReadOnlyList<MasterGameRequest>> GetAllMasterGameRequests()
        {
            var sql = "select * from tbl_mastergame_request where Answered = 0";

            using (var connection = new MySqlConnection(_connectionString))
            {
                IEnumerable<MasterGameRequestEntity> entities = await connection.QueryAsync<MasterGameRequestEntity>(sql);
                return await ConvertMasterGameEntities(entities);
            }
        }

        public async Task CompleteMasterGameRequest(MasterGameRequest masterGameRequest, Instant responseTime, string responseNote, Maybe<MasterGame> masterGame)
        {
            Guid? masterGameID = null;
            if (masterGame.HasValue)
            {
                masterGameID = masterGame.Value.MasterGameID;
            }
            string sql = "update tbl_mastergame_request set Answered = 1, ResponseTimestamp = @responseTime, " +
                         "ResponseNote = @responseNote, MasterGameID = @masterGameID where RequestID = @requestID;";
            using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.ExecuteAsync(sql,
                    new
                    {
                        requestID = masterGameRequest.RequestID,
                        masterGameID,
                        responseTime = responseTime.ToDateTimeUtc(),
                        responseNote
                    });
            }
        }

        public async Task<IReadOnlyList<MasterGameRequest>> GetMasterGameRequestsForUser(FantasyCriticUser user)
        {
            var sql = "select * from tbl_mastergame_request where UserID = @userID and Hidden = 0";

            using (var connection = new MySqlConnection(_connectionString))
            {
                IEnumerable<MasterGameRequestEntity> entities = await connection.QueryAsync<MasterGameRequestEntity>(sql, new { userID = user.UserID });
                return await ConvertMasterGameEntities(entities);
            }
        }

        private async Task<IReadOnlyList<MasterGameRequest>> ConvertMasterGameEntities(IEnumerable<MasterGameRequestEntity> entities)
        {
            var eligibilityLevels = await GetEligibilityLevels();
            var masterGames = await GetMasterGames();
            var users = await _userStore.GetAllUsers();
            List<MasterGameRequest> domainRequests = new List<MasterGameRequest>();
            foreach (var entity in entities)
            {
                EligibilityLevel eligibilityLevel = eligibilityLevels.Single(x => x.Level == entity.EligibilityLevel);
                Maybe<MasterGame> masterGame = Maybe<MasterGame>.None;
                if (entity.MasterGameID.HasValue)
                {
                    masterGame = masterGames.Single(x => x.MasterGameID == entity.MasterGameID.Value);
                }

                MasterGameRequest domain = entity.ToDomain(users.Single(x => x.UserID == entity.UserID), eligibilityLevel, masterGame);
                domainRequests.Add(domain);
            }

            return domainRequests;
        }

        public async Task<Maybe<MasterGameRequest>> GetMasterGameRequest(Guid requestID)
        {
            var sql = "select * from tbl_mastergame_request where RequestID = @requestID";

            using (var connection = new MySqlConnection(_connectionString))
            {
                MasterGameRequestEntity entity = await connection.QuerySingleOrDefaultAsync<MasterGameRequestEntity>(sql, new { requestID });
                if (entity == null)
                {
                    return Maybe<MasterGameRequest>.None;
                }

                var eligibilityLevel = await GetEligibilityLevel(entity.EligibilityLevel);
                Maybe<MasterGame> masterGame = Maybe<MasterGame>.None;
                if (entity.MasterGameID.HasValue)
                {
                    masterGame = await GetMasterGame(entity.MasterGameID.Value);
                }

                var user = await _userStore.FindByIdAsync(entity.UserID.ToString(), CancellationToken.None);

                return entity.ToDomain(user, eligibilityLevel, masterGame);
            }
        }

        public async Task DeleteMasterGameRequest(MasterGameRequest request)
        {
            var deleteObject = new
            {
                requestID = request.RequestID
            };

            using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.ExecuteAsync(
                    "delete from tbl_mastergame_request where RequestID = @requestID;",
                    deleteObject);
            }
        }

        public async Task DismissMasterGameRequest(MasterGameRequest masterGameRequest)
        {
            var dismissObject = new
            {
                requestID = masterGameRequest.RequestID
            };

            using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.ExecuteAsync(
                    "update tbl_mastergame_request SET Hidden = 1 where RequestID = @requestID;",
                    dismissObject);
            }
        }
    }
}
