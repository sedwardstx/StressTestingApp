using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace StressWeb.Models
{
    [DataContract]
    public class StressRecord
    {
        [DataMember]
        public Guid AgentId { get; set; }
        [DataMember]
        public StressModel Model { get; set; }
    }
}
