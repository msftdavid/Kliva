﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Security.Authentication.Web;
using GalaSoft.MvvmLight.Threading;
using Kliva.Models;
using Kliva.Services.Interfaces;
using Microsoft.Practices.ServiceLocation;
using Newtonsoft.Json;

namespace Kliva.Services
{
    public enum StravaServiceStatus
    {
        Failed,
        Success
    }

    public class StravaServiceEventArgs : EventArgs
    {
        public StravaServiceStatus Status { get; private set; }
        public Exception Exception { get; private set; }

        public StravaServiceEventArgs(StravaServiceStatus status, Exception ex = null)
        {
            Status = status;
            Exception = ex;
        }
    }

    public class StravaService : IStravaService
    {
        public IStravaActivityService StravaActivityService => ServiceLocator.Current.GetInstance<IStravaActivityService>();

        public IStravaAthleteService StravaAthleteService => ServiceLocator.Current.GetInstance<IStravaAthleteService>();

        public IStravaClubService StravaClubService => ServiceLocator.Current.GetInstance<IStravaClubService>();

        private string ParseAuthorizationResponse(string responseData)
        {
            var authorizationCodeIndex = responseData.IndexOf("&code=", StringComparison.Ordinal) + 6;
            return responseData.Substring(authorizationCodeIndex, responseData.Length - authorizationCodeIndex);
        }

        private async Task GetAccessToken(string authorizationCode)
        {
            try
            {
                var values = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("client_id", StravaIdentityConstants.STRAVA_AUTHORITY_CLIENT_ID),
                        new KeyValuePair<string, string>("client_secret", StravaIdentityConstants.STRAVA_AUTHORITY_CLIENT_SECRET),
                        new KeyValuePair<string, string>("code", authorizationCode)
                    };

                var httpClient = new HttpClient(new HttpClientHandler());
                var response = await httpClient.PostAsync(Constants.STRAVA_AUTHORITY_TOKEN_URL, new FormUrlEncodedContent(values));
                response.EnsureSuccessStatusCode();
                var responseString = await response.Content.ReadAsStringAsync();

                var accessToken = JsonConvert.DeserializeObject<AccessToken>(responseString);
                await ServiceLocator.Current.GetInstance<ISettingsService>().SetStravaAccessTokenAsync(accessToken.Token);

                OnStatusEvent(new StravaServiceEventArgs(StravaServiceStatus.Success));
            }
            catch(Exception ex)
            {
                OnStatusEvent(new StravaServiceEventArgs(StravaServiceStatus.Failed, ex));
            }
        }

        private async Task GetActivitySummaryRelationsAsync(IEnumerable<ActivitySummary> activities)
        {
            var results = (from activity in activities
                           select new
                           {
                               Activity = activity,
                               AthleteTask = StravaAthleteService.GetAthleteAsync(activity.AthleteMeta.Id.ToString()),
                               PhotoTask = (activity.AthleteMeta.Id == StravaAthleteService.Athlete.Id && activity.TotalPhotoCount > 0) ? StravaActivityService.GetPhotosAsync(activity.Id.ToString()) : Task.FromResult<List<Photo>>(null)
                           }).ToList();

            List<Task> tasks = new List<Task>();
            tasks.AddRange(results.Select(t => t.AthleteTask));
            tasks.AddRange(results.Select(t => t.PhotoTask));

            await Task.WhenAll(tasks);

            foreach (var pair in results)
            {
                var actualPair = pair;
                DispatcherHelper.CheckBeginInvokeOnUI(() =>
                {
                    actualPair.Activity.Athlete = actualPair.AthleteTask.Result;
                    actualPair.Activity.AllPhotos = actualPair.PhotoTask.Result;
                });
            }
        }

        #region Event handlers
        public event EventHandler<StravaServiceEventArgs> StatusEvent;

        protected virtual void OnStatusEvent(StravaServiceEventArgs e)
        {
            EventHandler<StravaServiceEventArgs> handler = StatusEvent;
            if (handler != null) handler(this, e);
        }
        #endregion

        public async Task GetAuthorizationCode()
        {
            string authenticationURL = string.Format("{0}?client_id={1}&response_type=code&redirect_uri={2}&scope=view_private&state=mystate&approval_prompt=force", Constants.STRAVA_AUTHORITY_AUTHORIZE_URL, StravaIdentityConstants.STRAVA_AUTHORITY_CLIENT_ID, Constants.STRAVA_AUTHORITY_REDIRECT_URL);

            try
            {
                WebAuthenticationResult webAuthenticationResult = await WebAuthenticationBroker.AuthenticateAsync(WebAuthenticationOptions.None, new Uri(authenticationURL), new Uri(Constants.STRAVA_AUTHORITY_REDIRECT_URL));
                if (webAuthenticationResult.ResponseStatus == WebAuthenticationStatus.Success)
                {
                    var responseData = webAuthenticationResult.ResponseData;
                    var tempAuthorizationCode = ParseAuthorizationResponse(responseData);
                    await GetAccessToken(tempAuthorizationCode);
                }
            }
            catch(Exception ex)
            {
                OnStatusEvent(new StravaServiceEventArgs(StravaServiceStatus.Failed, ex));
            }
        }

        public Task<Athlete> GetAthleteAsync()
        {
            return StravaAthleteService.GetAthleteAsync();
        }

        public async Task<Activity> GetActivityAsync(string id, bool includeEfforts)
        {
            //TODO: Glenn - kick of tasks in Task.Run List<Task>
            Activity activity = await StravaActivityService.GetActivityAsync(id, includeEfforts);

            if (activity != null)
            {
                await GetActivitySummaryRelationsAsync(new List<ActivitySummary> { activity });

                if (activity.OtherAthleteCount > 0)
                {
                    activity.RelatedActivities = await StravaActivityService.GetRelatedActivitiesAsync(id);
                    await GetActivitySummaryRelationsAsync(activity.RelatedActivities);
                }

                if (activity.KudosCount > 0)
                    activity.Kudos = await StravaActivityService.GetKudosAsync(id);

                if (activity.CommentCount > 0)
                    activity.Comments = await StravaActivityService.GetCommentsAsync(id);
                }

            return activity;
        }

        public async Task<IEnumerable<ActivitySummary>> GetActivitiesWithAthletesAsync(int page, int perPage, ActivityFeedFilter filter)
        {
            IList<ActivitySummary> activities = null;
            switch (filter)
            {
                case ActivityFeedFilter.All:
                case ActivityFeedFilter.Followers:
                    activities = await StravaActivityService.GetFollowersActivitiesAsync(page, perPage);
                    break;
                case ActivityFeedFilter.My:
                    activities = await StravaActivityService.GetActivitiesAsync(page, perPage);
                    break;
            }

            if (activities != null && activities.Any())
            {
                await GetActivitySummaryRelationsAsync(activities);

                if (filter == ActivityFeedFilter.Followers)
                    activities = activities.Where(activity => activity.Athlete.Id != StravaAthleteService.Athlete.Id).ToList();
            }

            return activities;
        }

        public Task GiveKudosAsync(string activityId)
        {
            return StravaActivityService.GiveKudosAsync(activityId);
        }

        public Task<List<ClubSummary>> GetClubsAsync()
        {
            return StravaClubService.GetClubsAsync();
        }

        public async Task<Club> GetClubAsync(string id)
        {
            //TODO: Glenn - kick of tasks in Task.Run List<Task>

            Club club = await StravaClubService.GetClubAsync(id);
            if (club != null)
            {
                if (club.MemberCount > 0)
                    club.Members = await StravaClubService.GetClubMembersAsync(id);
            }

            return club;
        }
    }
}