﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NodaTime;

namespace FantasyCritic.Lib.Domain.LeagueActions
{
    public record ActionProcessingSetMetadata(Guid ProcessSetID, Instant ProcessTime, string ProcessName);
}