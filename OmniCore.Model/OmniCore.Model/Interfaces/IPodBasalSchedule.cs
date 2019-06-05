﻿using System;
using System.Collections.Generic;
using System.Text;

namespace OmniCore.Model.Interfaces
{
    public interface IPodBasalSchedule
    {
        uint? Id { get; set; }
        DateTime Created { get; set; }
        Guid PodId { get; set; }

        int UtcOffset { get; set; }
        decimal[] BasalSchedule { get; set; }
        DateTime PodDateTime { get; set; }

        DateTime Updated { get; set; }
    }
}