﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace OmniCore.Model.Interfaces
{
    public interface IRadioProvider
    {
        Task<IRadio> FirstAvailable();
    }
}