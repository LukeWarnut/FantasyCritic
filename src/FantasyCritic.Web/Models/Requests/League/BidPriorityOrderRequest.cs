using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FantasyCritic.Web.Models.Requests.League
{
    public class BidPriorityOrderRequest
    {
        [Required]
        public Guid PublisherID { get; set; }
        [Required]
        public List<Guid> BidPriorities { get; set; }
    }
}