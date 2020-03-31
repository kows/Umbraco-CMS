﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Umbraco.Core.Configuration;
using Umbraco.Web.BackOffice.AspNetCore;

namespace Umbraco.Tests.Integration.Implementations
{
    
    public class TestHostingEnvironment : AspNetCoreHostingEnvironment, Umbraco.Core.Hosting.IHostingEnvironment
    {
        public TestHostingEnvironment(IHostingSettings hostingSettings, IWebHostEnvironment webHostEnvironment, IHttpContextAccessor httpContextAccessor)
            : base(hostingSettings, webHostEnvironment, httpContextAccessor)
        {
        }

        /// <summary>
        /// Override for tests since we are not hosted
        /// </summary>
        /// <remarks>
        /// This is specifically used by IOHelper and we want this to return false so that the root path is manually calcualted which is what we want for tests.
        /// </remarks>
        bool Umbraco.Core.Hosting.IHostingEnvironment.IsHosted { get; } = false;
    }
}
