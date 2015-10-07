﻿using Kliva.Models;
using System.Threading.Tasks;

namespace Kliva.Services.Interfaces
{
    public interface ISettingsService : IApplicationInfoService
    {
        Task<string> GetStoredStravaAccessToken();
        Task SetStravaAccessToken(string stravaAccessToken);
        Task RemoveStravaAccessToken();

        Task<DistanceUnitType> GetStoredDistanceUnitType();
        Task SetDistanceUnitType(DistanceUnitType distanceUnitType);

        //Task<AppVersion> GetStoredAppVersion();
    }
}