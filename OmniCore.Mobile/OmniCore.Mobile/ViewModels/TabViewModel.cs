﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using OmniCore.Mobile.Views.Pod;
using Xamarin.Forms;

namespace OmniCore.Mobile.ViewModels
{
    public class TabViewModel : BaseViewModel
    {
        public string Title { get; internal set; }
        public Page Content { get; internal set; }

        protected override async Task<BaseViewModel> BindData()
        {
            return this;
        }

        protected override void OnDisposeManagedResources()
        {
        }
    }
}
